using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HajdeAno.Core.Storage;

/// <summary>A case (header plus dictionary), stored as a JSON file in the session folder.</summary>
public sealed class CaseFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Incremented on every save.</summary>
    public long Revision { get; set; }

    /// <summary>Number of documents anonymized within this case (no content is stored).</summary>
    public int DocumentCount { get; set; }

    /// <summary>Unfinished input text of the case.</summary>
    public string? Draft { get; set; }

    public CaseDictionary Dictionary { get; set; } = new();
}

/// <summary>The folder is not a Hajde Ano session or the session file is damaged.</summary>
public sealed class SessionException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>Result of loading all cases.</summary>
/// <param name="Cases">The readable cases, most recently modified first.</param>
/// <param name="Unreadable">Case files that could not be read (damaged).</param>
public sealed record LoadedCases(List<CaseFile> Cases, int Unreadable);

/// <summary>
/// A folder chosen by the user in which the cases are stored, optionally encrypted with a
/// password.
/// </summary>
/// <remarks>
/// <para>Layout of the session folder:</para>
/// <list type="bullet">
/// <item><c>hajde-ano-session.json</c>: marks the folder as a session. For an encrypted
/// session it also holds the salt, the iteration count and an encrypted password check
/// (no private data).</item>
/// <item><c>cases/&lt;id&gt;.json</c>: one plain JSON file per case (unencrypted session), or
/// <c>cases/&lt;id&gt;.case</c>: one AES-256-GCM encrypted file per case (encrypted
/// session). The id is random and reveals nothing.</item>
/// </list>
/// <para>Writes go to a temporary file which is then renamed, so a crash never leaves a
/// half-written file. In an encrypted session, only ciphertext is ever written.</para>
/// </remarks>
public sealed class Session : IDisposable
{
    public const string MarkerFileName = "hajde-ano-session.json";
    private const string CasesDirName = "cases";
    private const string PlainExtension = ".json";
    private const string EncryptedExtension = ".case";
    private const string Kdf = "PBKDF2-HMAC-SHA256";
    private const int CurrentVersion = 1;
    private const int MinPasswordLength = 8;
    private static readonly byte[] Verifier = "hajde-ano session verifier v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    /// <summary>Key of the case files; null for an unencrypted session.</summary>
    private byte[]? _casesKey;
    private bool _disposed;

    private Session(string directory, byte[]? casesKey)
    {
        Directory = Path.GetFullPath(directory);
        _casesKey = casesKey;
    }

    /// <summary>Folder of the session.</summary>
    public string Directory { get; }

    /// <summary>Name shown to the user: the name of the folder.</summary>
    public string Name => NameOf(Directory);

    /// <summary>Whether the cases are encrypted with a password.</summary>
    public bool IsEncrypted => _casesKey is not null;

    private string CasesDir => Path.Combine(Directory, CasesDirName);

    private string CaseExtension => IsEncrypted ? EncryptedExtension : PlainExtension;

    private sealed class Marker
    {
        public int Version { get; set; } = CurrentVersion;

        /// <summary>Present only for encrypted sessions.</summary>
        public EncryptionHeader? Encryption { get; set; }
    }

    private sealed class EncryptionHeader
    {
        public string Kdf { get; set; } = Session.Kdf;

        public int Iterations { get; set; }

        public string Salt { get; set; } = "";

        public string Check { get; set; } = "";
    }

    /// <summary>Name of the session in <paramref name="directory"/> (its folder name).</summary>
    public static string NameOf(string directory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>Whether <paramref name="directory"/> holds a session.</summary>
    public static bool IsSession(string directory) =>
        File.Exists(Path.Combine(directory, MarkerFileName));

    /// <summary>
    /// Whether the session in <paramref name="directory"/> is encrypted, i.e. needs a
    /// password to open. Throws <see cref="SessionException"/> if it is not a valid session.
    /// </summary>
    public static bool IsEncryptedSession(string directory) =>
        ReadMarker(directory).Encryption is not null;

    /// <summary>
    /// Create a new session in <paramref name="directory"/>, which must not exist yet or be
    /// empty (so that the cases do not get mixed with other files). If a
    /// <paramref name="password"/> is given, the cases are encrypted with a key derived
    /// from it.
    /// </summary>
    public static Session Create(
        string directory,
        string? password = null,
        int iterations = Crypto.DefaultIterations
    )
    {
        if (IsSession(directory))
        {
            throw new InvalidOperationException("The folder already holds a session");
        }

        if (
            System.IO.Directory.Exists(directory)
            && System.IO.Directory.EnumerateFileSystemEntries(directory).Any()
        )
        {
            throw new SessionException(
                "The folder is not empty. Choose an empty folder for a new session"
            );
        }

        if (password is not null && password.Length < MinPasswordLength)
        {
            throw new ArgumentException(
                $"The password must have at least {MinPasswordLength} characters"
            );
        }

        var marker = new Marker();
        byte[]? casesKey = null;
        if (password is not null)
        {
            var salt = Crypto.RandomBytes(Crypto.SaltSize);
            var key = Crypto.DeriveKey(password, salt, iterations);
            try
            {
                marker.Encryption = new EncryptionHeader
                {
                    Iterations = iterations,
                    Salt = Convert.ToBase64String(salt),
                    Check = Convert.ToBase64String(Crypto.Encrypt(CheckKey(key), Verifier)),
                };
                casesKey = Crypto.DeriveSubKey(key, "hajde-ano session cases");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        var session = new Session(directory, casesKey);
        CreatePrivateDirectory(session.Directory);
        CreatePrivateDirectory(session.CasesDir);
        AtomicWrite(
            Path.Combine(session.Directory, MarkerFileName),
            JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions)
        );
        return session;
    }

    /// <summary>
    /// Open an existing session. Throws <see cref="SessionException"/> if the folder is not
    /// a session, is damaged or was created by a newer version, and
    /// <see cref="UnauthorizedAccessException"/> if the session is encrypted and the
    /// password is missing or wrong.
    /// </summary>
    public static Session Open(string directory, string? password = null)
    {
        var marker = ReadMarker(directory);
        byte[]? casesKey = null;
        if (marker.Encryption is { } enc)
        {
            if (password is null)
            {
                throw new UnauthorizedAccessException("The session is encrypted");
            }

            byte[] salt,
                check;
            try
            {
                salt = Convert.FromBase64String(enc.Salt);
                check = Convert.FromBase64String(enc.Check);
            }
            catch (FormatException e)
            {
                throw new SessionException("The session file is damaged", e);
            }

            if (salt.Length != Crypto.SaltSize || check.Length > 1024)
            {
                throw new SessionException("The session file is damaged");
            }

            var key = Crypto.DeriveKey(password, salt, enc.Iterations);
            try
            {
                byte[] verifier;
                try
                {
                    verifier = Crypto.Decrypt(CheckKey(key), check);
                }
                catch (CryptographicException)
                {
                    throw new UnauthorizedAccessException("Wrong password");
                }

                if (!verifier.AsSpan().SequenceEqual(Verifier))
                {
                    throw new UnauthorizedAccessException("Wrong password");
                }

                casesKey = Crypto.DeriveSubKey(key, "hajde-ano session cases");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        else if (password is not null)
        {
            throw new SessionException("The session is not encrypted");
        }

        var session = new Session(directory, casesKey);
        CreatePrivateDirectory(session.CasesDir);
        session.CleanUp();
        return session;
    }

    private static byte[] CheckKey(byte[] key) =>
        Crypto.DeriveSubKey(key, "hajde-ano session check");

    private static Marker ReadMarker(string directory)
    {
        var path = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(path))
        {
            throw new SessionException("The folder is not a Hajde Ano session");
        }

        if (new FileInfo(path).Length > 64 * 1024)
        {
            throw new SessionException("The session file is damaged");
        }

        Marker marker;
        try
        {
            marker = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new SessionException("The session file is empty");
        }
        catch (JsonException e)
        {
            throw new SessionException("The session file is damaged", e);
        }

        if (marker.Version is < 1 or > CurrentVersion)
        {
            throw new SessionException("The session was created by a newer or unknown version");
        }

        if (marker.Encryption is { } enc)
        {
            if (enc.Kdf != Kdf)
            {
                throw new SessionException("Unknown key derivation function");
            }

            // Bounded, so that a damaged or manipulated file cannot make opening hang.
            if (enc.Iterations is < Crypto.MinIterations or > Crypto.MaxIterations)
            {
                throw new SessionException("Invalid iteration count");
            }
        }

        return marker;
    }

    private static void CheckId(string id)
    {
        if (id.Length == 0 || id.Length > 64 || !id.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Invalid case id", nameof(id));
        }
    }

    private string CasePath(string id)
    {
        CheckId(id);
        return Path.Combine(CasesDir, id + CaseExtension);
    }

    private byte[] CasesKey =>
        _disposed ? throw new ObjectDisposedException(nameof(Session)) : _casesKey!;

    /// <summary>Whether the case is part of the session.</summary>
    public bool Contains(string id) => File.Exists(CasePath(id));

    /// <summary>Save the case (atomically; encrypted in an encrypted session).</summary>
    public void Save(CaseFile caseFile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckId(caseFile.Id);
        caseFile.Modified = DateTimeOffset.UtcNow;
        caseFile.Revision++;
        var json = JsonSerializer.SerializeToUtf8Bytes(caseFile, JsonOptions);
        if (!IsEncrypted)
        {
            AtomicWrite(CasePath(caseFile.Id), json);
            return;
        }

        try
        {
            AtomicWrite(
                CasePath(caseFile.Id),
                Crypto.Encrypt(CasesKey, json, Encoding.ASCII.GetBytes(caseFile.Id))
            );
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>
    /// Load a case. Throws <see cref="FileNotFoundException"/> if it does not exist and
    /// <see cref="InvalidDataException"/> if the file is damaged or was tampered with.
    /// </summary>
    public CaseFile Load(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = CasePath(id);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The case does not exist");
        }

        var data = File.ReadAllBytes(path);
        if (IsEncrypted)
        {
            try
            {
                // The id is authenticated, so that case files cannot be swapped.
                data = Crypto.Decrypt(CasesKey, data, Encoding.ASCII.GetBytes(id));
            }
            catch (CryptographicException e)
            {
                throw new InvalidDataException("The case file is damaged", e);
            }
        }

        CaseFile? caseFile;
        try
        {
            caseFile = JsonSerializer.Deserialize<CaseFile>(data, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Invalid case file", e);
        }
        finally
        {
            if (IsEncrypted)
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }

        if (caseFile is null || caseFile.Id != id)
        {
            throw new InvalidDataException("Invalid case file");
        }

        return caseFile;
    }

    /// <summary>Load all cases. Unreadable files are skipped and counted.</summary>
    public LoadedCases LoadAll()
    {
        var cases = new List<CaseFile>();
        var unreadable = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(CasesDir, "*" + CaseExtension))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                cases.Add(Load(id));
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException)
            {
                unreadable++;
            }
        }

        cases.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return new LoadedCases(cases, unreadable);
    }

    /// <summary>Delete a case.</summary>
    public void Delete(string id) => File.Delete(CasePath(id));

    /// <summary>Remove temporary files of interrupted writes.</summary>
    private void CleanUp()
    {
        foreach (
            var path in System
                .IO.Directory.EnumerateFiles(CasesDir, "*.tmp")
                .Concat(System.IO.Directory.EnumerateFiles(Directory, "*.tmp"))
        )
        {
            File.Delete(path);
        }
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
    /// Files are readable by the user only (on Linux and macOS).
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

    /// <summary>Forget the key (zeroed in memory).</summary>
    public void Dispose()
    {
        if (_casesKey is not null)
        {
            CryptographicOperations.ZeroMemory(_casesKey);
        }

        _disposed = true;
    }
}
