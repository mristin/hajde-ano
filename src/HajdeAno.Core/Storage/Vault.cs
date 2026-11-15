using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HajdeAno.Core.Storage;

/// <summary>A case (header plus dictionary), stored encrypted on disk.</summary>
public sealed class CaseFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Incremented on every save and recorded in the encrypted index, so that an older copy
    /// of the case file put back on disk is detected.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>Number of documents anonymized within this case (no content is stored).</summary>
    public int DocumentCount { get; set; }

    /// <summary>
    /// Unfinished input text of the case. Contains private data and is therefore only ever
    /// stored inside the encrypted case file.
    /// </summary>
    public string? Draft { get; set; }

    public CaseDictionary Dictionary { get; set; } = new();
}

/// <summary>The vault file is damaged or not a Hajde Ano vault.</summary>
public sealed class VaultCorruptException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>Result of loading all cases.</summary>
/// <param name="Cases">The readable cases, most recently modified first.</param>
/// <param name="Unreadable">Case files that could not be decrypted (damaged or manipulated).</param>
/// <param name="Outdated">Case files older than recorded in the index (an old copy was put back).</param>
public sealed record LoadedCases(List<CaseFile> Cases, int Unreadable, int Outdated)
{
    public void Deconstruct(out List<CaseFile> cases, out int unreadable)
    {
        cases = Cases;
        unreadable = Unreadable + Outdated;
    }
}

/// <summary>
/// Password-protected store of cases.
/// </summary>
/// <remarks>
/// <para>Layout of the vault directory:</para>
/// <list type="bullet">
/// <item><c>vault.json</c>: KDF parameters, salt and a password check (no private data).</item>
/// <item><c>index</c>: encrypted list of cases with a random key and the revision of each.</item>
/// <item><c>cases/&lt;name&gt;.case</c>: one encrypted file per case; the name is a keyed hash
/// (HMAC) of the case id, so file names reveal nothing.</item>
/// </list>
/// <para>Every case has its own random key, kept only in the encrypted index. Deleting a case
/// removes its key, so that copies of the case file left behind on disk (e.g. in free SSD
/// blocks or backups) can no longer be decrypted with the current vault. Writes go to a
/// temporary file which is then renamed, so a crash never leaves a half-written file.</para>
/// </remarks>
public sealed class Vault : IDisposable
{
    private const string VaultFileName = "vault.json";
    private const string IndexFileName = "index";
    private const string CasesDirName = "cases";
    private const string CaseExtension = ".case";
    private const string Kdf = "PBKDF2-HMAC-SHA256";
    private const int CurrentVersion = 2;
    private static readonly byte[] Verifier = "hajde-ano vault verifier v1"u8.ToArray();
    private static readonly byte[] IndexAssociatedData = "hajde-ano index v2"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    private byte[]? _indexKey;
    private byte[]? _namesKey;
    private Index _index;

    private Vault(string directory, byte[] masterKey, Index index)
    {
        Directory = directory;
        _indexKey = Crypto.DeriveSubKey(masterKey, "hajde-ano index");
        _namesKey = Crypto.DeriveSubKey(masterKey, "hajde-ano file names");
        _index = index;
    }

    /// <summary>Directory holding the vault metadata and the encrypted cases.</summary>
    public string Directory { get; }

    private string CasesDir => Path.Combine(Directory, CasesDirName);

    private string IndexPath => Path.Combine(Directory, IndexFileName);

    private byte[] IndexKey => _indexKey ?? throw new ObjectDisposedException(nameof(Vault));

    private byte[] NamesKey => _namesKey ?? throw new ObjectDisposedException(nameof(Vault));

    private sealed class Header
    {
        public int Version { get; set; } = CurrentVersion;

        public string Kdf { get; set; } = Vault.Kdf;

        public int Iterations { get; set; }

        public string Salt { get; set; } = "";

        public string Check { get; set; } = "";
    }

    private sealed class IndexEntry
    {
        public string Key { get; set; } = "";

        public long Revision { get; set; }
    }

    private sealed class Index
    {
        public Dictionary<string, IndexEntry> Cases { get; set; } = [];
    }

    public static bool Exists(string directory) =>
        File.Exists(Path.Combine(directory, VaultFileName));

    /// <summary>Create a new vault protected by <paramref name="password"/>.</summary>
    public static Vault Create(
        string directory,
        string password,
        int iterations = Crypto.DefaultIterations
    )
    {
        if (Exists(directory))
        {
            throw new InvalidOperationException("A vault already exists in this directory");
        }

        if (password.Length < 8)
        {
            throw new ArgumentException("The password must have at least 8 characters");
        }

        CreatePrivateDirectory(directory);
        var salt = Crypto.RandomBytes(Crypto.SaltSize);
        var key = Crypto.DeriveKey(password, salt, iterations);
        try
        {
            var vault = new Vault(directory, key, new Index());
            CreatePrivateDirectory(vault.CasesDir);
            vault.WriteIndex();
            WriteHeader(
                directory,
                new Header
                {
                    Iterations = iterations,
                    Salt = Convert.ToBase64String(salt),
                    Check = Convert.ToBase64String(Crypto.Encrypt(key, Verifier)),
                }
            );
            return vault;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Open an existing vault. Throws <see cref="UnauthorizedAccessException"/> on a wrong
    /// password and <see cref="VaultCorruptException"/> if the vault files are damaged.
    /// </summary>
    public static Vault Open(string directory, string password)
    {
        var header = ReadHeader(directory);
        var key = Crypto.DeriveKey(password, header.SaltBytes, header.Iterations);
        try
        {
            try
            {
                var check = Crypto.Decrypt(key, header.CheckBytes);
                if (!check.AsSpan().SequenceEqual(Verifier))
                {
                    throw new CryptographicException();
                }
            }
            catch (CryptographicException)
            {
                throw new UnauthorizedAccessException("Wrong password");
            }

            CreatePrivateDirectory(Path.Combine(directory, CasesDirName));
            if (header.Version == 1)
            {
                return MigrateFromVersion1(directory, key, header.Raw);
            }

            var vault = new Vault(directory, key, new Index());
            vault._index = vault.ReadIndex();
            vault.CleanUp();
            return vault;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed record ValidHeader(
        Header Raw,
        int Version,
        int Iterations,
        byte[] SaltBytes,
        byte[] CheckBytes
    );

    private static ValidHeader ReadHeader(string directory)
    {
        var path = Path.Combine(directory, VaultFileName);
        if (new FileInfo(path).Length > 64 * 1024)
        {
            throw new VaultCorruptException("The vault file is too large");
        }

        try
        {
            var header = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new VaultCorruptException("The vault file is empty");
            if (header.Version is < 1 or > CurrentVersion)
            {
                throw new VaultCorruptException(
                    "The vault was created by a newer or unknown version"
                );
            }

            if (header.Kdf != Kdf)
            {
                throw new VaultCorruptException("Unknown key derivation function");
            }

            if (header.Iterations is < Crypto.MinIterations or > Crypto.MaxIterations)
            {
                throw new VaultCorruptException("Invalid iteration count");
            }

            var salt = Convert.FromBase64String(header.Salt);
            var check = Convert.FromBase64String(header.Check);
            if (salt.Length != Crypto.SaltSize || check.Length > 1024)
            {
                throw new VaultCorruptException("Invalid salt or password check");
            }

            return new ValidHeader(header, header.Version, header.Iterations, salt, check);
        }
        catch (Exception e) when (e is JsonException or FormatException or NotSupportedException)
        {
            throw new VaultCorruptException("The vault file is damaged", e);
        }
    }

    private static void WriteHeader(string directory, Header header) =>
        AtomicWrite(
            Path.Combine(directory, VaultFileName),
            JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions)
        );

    private Index ReadIndex()
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(IndexPath);
        }
        catch (FileNotFoundException e)
        {
            throw new VaultCorruptException("The case index is missing", e);
        }

        byte[] plaintext;
        try
        {
            plaintext = Crypto.Decrypt(IndexKey, data, IndexAssociatedData);
        }
        catch (CryptographicException e)
        {
            throw new VaultCorruptException("The case index is damaged", e);
        }

        try
        {
            return JsonSerializer.Deserialize<Index>(plaintext, JsonOptions)
                ?? throw new VaultCorruptException("The case index is empty");
        }
        catch (JsonException e)
        {
            throw new VaultCorruptException("The case index is damaged", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void WriteIndex()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_index, JsonOptions);
        try
        {
            AtomicWrite(IndexPath, Crypto.Encrypt(IndexKey, plaintext, IndexAssociatedData));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void CheckId(string id)
    {
        if (id.Length == 0 || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Invalid case id", nameof(id));
        }
    }

    /// <summary>File name of the case: a keyed hash of its id, base64url-encoded.</summary>
    private string CasePath(string id)
    {
        CheckId(id);
        var mac = HMACSHA256.HashData(NamesKey, Encoding.ASCII.GetBytes(id));
        return Path.Combine(CasesDir, Base64Url.EncodeToString(mac) + CaseExtension);
    }

    /// <summary>Whether the case is part of the vault.</summary>
    public bool Contains(string id) => _index.Cases.ContainsKey(id);

    /// <summary>Encrypt and save the case (atomically) and record its new revision.</summary>
    public void Save(CaseFile caseFile) => Save(caseFile, touch: true);

    private void Save(CaseFile caseFile, bool touch)
    {
        CheckId(caseFile.Id);
        if (!_index.Cases.TryGetValue(caseFile.Id, out var entry))
        {
            // Record the key first, so that the case file can always be decrypted.
            entry = new IndexEntry
            {
                Key = Convert.ToBase64String(Crypto.RandomBytes(Crypto.KeySize)),
                Revision = 0,
            };
            _index.Cases[caseFile.Id] = entry;
            WriteIndex();
        }

        if (touch)
        {
            caseFile.Modified = DateTimeOffset.UtcNow;
        }

        caseFile.Revision = Math.Max(caseFile.Revision, entry.Revision) + 1;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(caseFile, JsonOptions);
        var key = Convert.FromBase64String(entry.Key);
        try
        {
            var data = Crypto.Encrypt(key, plaintext, Encoding.ASCII.GetBytes(caseFile.Id));
            AtomicWrite(CasePath(caseFile.Id), data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }

        // A crash right here leaves the case newer than the index, which is accepted.
        entry.Revision = caseFile.Revision;
        WriteIndex();
    }

    /// <summary>
    /// Load a case. Throws <see cref="CryptographicException"/> if the file is damaged or
    /// manipulated and <see cref="InvalidDataException"/> if it is older than recorded.
    /// </summary>
    public CaseFile Load(string id)
    {
        var path = CasePath(id);
        if (!_index.Cases.TryGetValue(id, out var entry))
        {
            throw new FileNotFoundException("The case does not exist");
        }

        var data = File.ReadAllBytes(path);
        var key = Convert.FromBase64String(entry.Key);
        byte[] plaintext;
        try
        {
            plaintext = Crypto.Decrypt(key, data, Encoding.ASCII.GetBytes(id));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            var caseFile = JsonSerializer.Deserialize<CaseFile>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Invalid case file");
            if (caseFile.Id != id)
            {
                throw new InvalidDataException("Case id mismatch");
            }

            if (caseFile.Revision < entry.Revision)
            {
                throw new OutdatedCaseException();
            }

            if (caseFile.Revision > entry.Revision)
            {
                // The program stopped between writing the case and the index.
                entry.Revision = caseFile.Revision;
                WriteIndex();
            }

            return caseFile;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>The case file is older than recorded in the index (an old copy was put back).</summary>
    public sealed class OutdatedCaseException()
        : IOException("The case file is older than its last saved version");

    /// <summary>Load all cases. Unreadable and outdated files are skipped and counted.</summary>
    public LoadedCases LoadAll()
    {
        var cases = new List<CaseFile>();
        var unreadable = 0;
        var outdated = 0;
        foreach (var id in _index.Cases.Keys.ToList())
        {
            try
            {
                cases.Add(Load(id));
            }
            catch (OutdatedCaseException)
            {
                outdated++;
            }
            catch (Exception e)
                when (e
                        is CryptographicException
                            or InvalidDataException
                            or JsonException
                            or IOException
                )
            {
                unreadable++;
            }
        }

        cases.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return new LoadedCases(cases, unreadable, outdated);
    }

    /// <summary>
    /// Delete a case: its key is removed from the index first (so that any copy of the file
    /// becomes undecryptable), then the file is overwritten with random bytes and deleted.
    /// </summary>
    public void Delete(string id)
    {
        var path = CasePath(id);
        if (_index.Cases.Remove(id))
        {
            WriteIndex();
        }

        Shred(path);
    }

    /// <summary>
    /// Remove leftovers: temporary files of interrupted writes and case files which are not
    /// in the index (e.g. of a deletion interrupted after the index was written).
    /// </summary>
    private void CleanUp()
    {
        var known = _index.Cases.Keys.Select(CasePath).ToHashSet(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFiles(CasesDir))
        {
            if (!known.Contains(path))
            {
                Shred(path);
            }
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.tmp"))
        {
            Shred(path);
        }
    }

    private static void Shred(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var length = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.Write(Crypto.RandomBytes((int)Math.Min(length, int.MaxValue)));
            fs.Flush(true);
        }

        File.Delete(path);
    }

    /// <summary>
    /// Version 1 encrypted all cases with the password key and named the files by case id.
    /// Re-encrypt them with per-case keys under hashed names, then remove the old files.
    /// </summary>
    private static Vault MigrateFromVersion1(string directory, byte[] key, Header header)
    {
        var vault = new Vault(directory, key, new Index());
        var oldFiles = System
            .IO.Directory.EnumerateFiles(vault.CasesDir, "*" + CaseExtension)
            .Where(p =>
                Path.GetFileNameWithoutExtension(p) is { Length: 32 } n
                && n.All(char.IsAsciiHexDigitLower)
            )
            .ToList();
        var cases = new List<CaseFile>();
        foreach (var path in oldFiles)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var plaintext = Crypto.Decrypt(
                    key,
                    File.ReadAllBytes(path),
                    Encoding.ASCII.GetBytes(id)
                );
                try
                {
                    var c = JsonSerializer.Deserialize<CaseFile>(plaintext, JsonOptions);
                    if (c is not null && c.Id == id)
                    {
                        cases.Add(c);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (Exception e) when (e is CryptographicException or JsonException) { }
        }

        foreach (var c in cases)
        {
            vault.Save(c, touch: false);
        }

        // Only now switch the vault to version 2; until then, the old files stay intact.
        header.Version = CurrentVersion;
        WriteHeader(directory, header);
        vault.CleanUp();
        return vault;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
        }
        else
        {
            System.IO.Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    /// <summary>
    /// Write via a temporary file in the same directory, flush it to the disk and rename it
    /// over the target, so that a crash leaves either the old or the new file, never a mix.
    /// The temporary file only ever contains ciphertext or non-sensitive metadata.
    /// </summary>
    private static void AtomicWrite(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var fs = new FileStream(tmp, options))
        {
            fs.Write(data);
            fs.Flush(true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        if (_indexKey is not null)
        {
            CryptographicOperations.ZeroMemory(_indexKey);
            _indexKey = null;
        }

        if (_namesKey is not null)
        {
            CryptographicOperations.ZeroMemory(_namesKey);
            _namesKey = null;
        }
    }
}
