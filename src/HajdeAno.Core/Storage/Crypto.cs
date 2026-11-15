using System.Security.Cryptography;
using System.Text;

namespace HajdeAno.Core.Storage;

/// <summary>
/// Authenticated encryption (AES-256-GCM) with a password-derived key (PBKDF2-SHA256).
/// </summary>
public static class Crypto
{
    public const int KeySize = 32;
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Lowest accepted iteration count when reading a file (tests use 1000).</summary>
    public const int MinIterations = 1000;

    /// <summary>
    /// Highest accepted iteration count when reading a file, so that a damaged or manipulated
    /// file cannot make unlocking hang.
    /// </summary>
    public const int MaxIterations = 10_000_000;

    private static readonly byte[] PasswordMagic = "HAJP"u8.ToArray();

    private static readonly byte[] Magic = "HAJ1"u8.ToArray();

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize
        );

    /// <summary>Derive an independent sub-key (HKDF-SHA256) for the given purpose.</summary>
    public static byte[] DeriveSubKey(byte[] key, string purpose) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            key,
            KeySize,
            info: Encoding.UTF8.GetBytes(purpose)
        );

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>Encrypt; the output is <c>magic | nonce | tag | ciphertext</c>.</summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext, byte[]? associatedData = null)
    {
        var nonce = RandomBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        var output = new byte[Magic.Length + NonceSize + TagSize + ciphertext.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        tag.CopyTo(output, Magic.Length + NonceSize);
        ciphertext.CopyTo(output, Magic.Length + NonceSize + TagSize);
        return output;
    }

    /// <summary>Decrypt; throws <see cref="CryptographicException"/> on a wrong key or tampering.</summary>
    public static byte[] Decrypt(byte[] key, byte[] data, byte[]? associatedData = null)
    {
        if (
            data.Length < Magic.Length + NonceSize + TagSize
            || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)
        )
        {
            throw new CryptographicException("Not an encrypted Hajde Ano file");
        }

        var nonce = data.AsSpan(Magic.Length, NonceSize);
        var tag = data.AsSpan(Magic.Length + NonceSize, TagSize);
        var ciphertext = data.AsSpan(Magic.Length + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    /// <summary>
    /// Encrypt a standalone file with a password; the output is
    /// <c>"HAJP" | iterations (int32 LE) | salt | Encrypt(...)</c>, and the header is
    /// authenticated as associated data.
    /// </summary>
    public static byte[] EncryptWithPassword(
        string password,
        byte[] plaintext,
        int iterations = DefaultIterations
    )
    {
        var header = new byte[PasswordMagic.Length + 4 + SaltSize];
        PasswordMagic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(PasswordMagic.Length, 4), iterations);
        RandomBytes(SaltSize).CopyTo(header, PasswordMagic.Length + 4);
        var key = DeriveKey(password, header[(PasswordMagic.Length + 4)..], iterations);
        try
        {
            return [.. header, .. Encrypt(key, plaintext, header)];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Whether the data looks like the output of <see cref="EncryptWithPassword"/>.</summary>
    public static bool IsPasswordEncrypted(byte[] data) =>
        data.Length >= PasswordMagic.Length
        && data.AsSpan(0, PasswordMagic.Length).SequenceEqual(PasswordMagic);

    /// <summary>
    /// Decrypt the output of <see cref="EncryptWithPassword"/>. Throws
    /// <see cref="UnauthorizedAccessException"/> on a wrong password and
    /// <see cref="InvalidDataException"/> on a damaged file.
    /// </summary>
    public static byte[] DecryptWithPassword(string password, byte[] data)
    {
        var headerLength = PasswordMagic.Length + 4 + SaltSize;
        if (data.Length < headerLength || !IsPasswordEncrypted(data))
        {
            throw new InvalidDataException("Not a password-encrypted Hajde Ano file");
        }

        var iterations = BitConverter.ToInt32(data, PasswordMagic.Length);
        if (iterations is < MinIterations or > MaxIterations)
        {
            throw new InvalidDataException("Invalid iteration count");
        }

        var header = data[..headerLength];
        var key = DeriveKey(password, header[(PasswordMagic.Length + 4)..], iterations);
        try
        {
            return Decrypt(key, data[headerLength..], header);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Wrong password or damaged file");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
