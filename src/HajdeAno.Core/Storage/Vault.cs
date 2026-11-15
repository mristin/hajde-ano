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

    /// <summary>Number of documents anonymized within this case (no content is stored).</summary>
    public int DocumentCount { get; set; }

    public CaseDictionary Dictionary { get; set; } = new();
}

/// <summary>
/// Password-protected store of cases. All case data (including case names) is encrypted
/// at rest with AES-256-GCM; the key is derived from the password and only kept in memory.
/// </summary>
public sealed class Vault : IDisposable
{
    private const string VaultFileName = "vault.json";
    private const string CasesDirName = "cases";
    private const string CaseExtension = ".case";
    private static readonly byte[] Verifier = "hajde-ano vault verifier v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    private byte[]? _key;

    private Vault(string directory, byte[] key)
    {
        Directory = directory;
        _key = key;
    }

    /// <summary>Directory holding the vault metadata and the encrypted cases.</summary>
    public string Directory { get; }

    private string CasesDir => Path.Combine(Directory, CasesDirName);

    private byte[] Key => _key ?? throw new ObjectDisposedException(nameof(Vault));

    private sealed class Header
    {
        public int Version { get; set; } = 1;

        public string Kdf { get; set; } = "PBKDF2-HMAC-SHA256";

        public int Iterations { get; set; }

        public string Salt { get; set; } = "";

        public string Check { get; set; } = "";
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

        System.IO.Directory.CreateDirectory(directory);
        var salt = Crypto.RandomBytes(Crypto.SaltSize);
        var key = Crypto.DeriveKey(password, salt, iterations);
        var header = new Header
        {
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            Check = Convert.ToBase64String(Crypto.Encrypt(key, Verifier)),
        };
        AtomicWrite(
            Path.Combine(directory, VaultFileName),
            JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions)
        );
        System.IO.Directory.CreateDirectory(Path.Combine(directory, CasesDirName));
        return new Vault(directory, key);
    }

    /// <summary>Open an existing vault; throws <see cref="UnauthorizedAccessException"/> on a wrong password.</summary>
    public static Vault Open(string directory, string password)
    {
        var header = JsonSerializer.Deserialize<Header>(
            File.ReadAllBytes(Path.Combine(directory, VaultFileName)),
            JsonOptions
        ) ?? throw new InvalidDataException("Invalid vault file");
        var key = Crypto.DeriveKey(
            password,
            Convert.FromBase64String(header.Salt),
            header.Iterations
        );
        try
        {
            var check = Crypto.Decrypt(key, Convert.FromBase64String(header.Check));
            if (!check.AsSpan().SequenceEqual(Verifier))
            {
                throw new CryptographicException();
            }
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new UnauthorizedAccessException("Wrong password");
        }

        System.IO.Directory.CreateDirectory(Path.Combine(directory, CasesDirName));
        return new Vault(directory, key);
    }

    private string CasePath(string id)
    {
        if (id.Length == 0 || !id.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Invalid case id", nameof(id));
        }

        return Path.Combine(CasesDir, id + CaseExtension);
    }

    public void Save(CaseFile caseFile)
    {
        caseFile.Modified = DateTimeOffset.UtcNow;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(caseFile, JsonOptions);
        try
        {
            var data = Crypto.Encrypt(Key, plaintext, Encoding.ASCII.GetBytes(caseFile.Id));
            AtomicWrite(CasePath(caseFile.Id), data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public CaseFile Load(string id)
    {
        var data = File.ReadAllBytes(CasePath(id));
        var plaintext = Crypto.Decrypt(Key, data, Encoding.ASCII.GetBytes(id));
        try
        {
            var caseFile = JsonSerializer.Deserialize<CaseFile>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("Invalid case file");
            if (caseFile.Id != id)
            {
                throw new InvalidDataException("Case id mismatch");
            }

            return caseFile;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Load all cases. Unreadable files are skipped and counted.</summary>
    public (List<CaseFile> Cases, int Unreadable) LoadAll()
    {
        var cases = new List<CaseFile>();
        var unreadable = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(CasesDir, "*" + CaseExtension))
        {
            try
            {
                cases.Add(Load(Path.GetFileNameWithoutExtension(path)));
            }
            catch (Exception e)
                when (e
                        is CryptographicException
                            or InvalidDataException
                            or JsonException
                            or ArgumentException
                )
            {
                unreadable++;
            }
        }

        cases.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return (cases, unreadable);
    }

    /// <summary>Delete a case. The file is overwritten with random bytes before deletion.</summary>
    public void Delete(string id)
    {
        var path = CasePath(id);
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
    /// Write via a temporary file in the same directory and rename. The temporary file only
    /// ever contains ciphertext or non-sensitive metadata.
    /// </summary>
    private static void AtomicWrite(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
