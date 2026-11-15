using System.Text;
using HajdeAno.Core.Storage;

namespace HajdeAno.Core.Tests;

public sealed class CryptoTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(
        """{"entities": [{"text": "Zora Blumenthal"}]}"""
    );

    [Fact]
    public void Password_encryption_round_trip()
    {
        var data = Crypto.EncryptWithPassword("synthetic password", Secret, iterations: 1000);
        Assert.True(Crypto.IsPasswordEncrypted(data));
        Assert.True(data.AsSpan().IndexOf("Blumenthal"u8) < 0);
        Assert.Equal(Secret, Crypto.DecryptWithPassword("synthetic password", data));
    }

    [Fact]
    public void Wrong_password_and_tampering_are_rejected()
    {
        var data = Crypto.EncryptWithPassword("synthetic password", Secret, iterations: 1000);
        Assert.Throws<UnauthorizedAccessException>(() =>
            Crypto.DecryptWithPassword("other password", data)
        );

        // The header (including the iteration count) is authenticated.
        var tampered = (byte[])data.Clone();
        tampered[4] ^= 0x01;
        Assert.Throws<UnauthorizedAccessException>(() =>
            Crypto.DecryptWithPassword("synthetic password", tampered)
        );
    }

    [Fact]
    public void Huge_iteration_counts_are_rejected_without_deriving_a_key()
    {
        var data = Crypto.EncryptWithPassword("synthetic password", Secret, iterations: 1000);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() =>
            Crypto.DecryptWithPassword("synthetic password", data)
        );
    }

    [Fact]
    public void Plain_json_is_not_mistaken_for_an_encrypted_file() =>
        Assert.False(Crypto.IsPasswordEncrypted(Secret));
}
