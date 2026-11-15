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

    private static readonly byte[] Magic = "HAJ1"u8.ToArray();

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize
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
}
