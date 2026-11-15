using System.Security.Cryptography;
using System.Text;
using HajdeAno.Core.Storage;

namespace HajdeAno.Core.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "hajde-ano-test-" + Guid.NewGuid().ToString("N")
    );

    // Low iteration count keeps the tests fast; production uses Crypto.DefaultIterations.
    private const int Iterations = 1000;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static CaseFile SampleCase()
    {
        var c = new CaseFile { Name = "Fall Muster-Testperson" };
        c.Dictionary.Assign("Anna Müller", Category.Person);
        return c;
    }

    [Fact]
    public void Round_trip()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);

        using var reopened = Vault.Open(_dir, "correct horse battery");
        var (cases, unreadable) = reopened.LoadAll();
        Assert.Equal(0, unreadable);
        var loaded = Assert.Single(cases);
        Assert.Equal("Fall Muster-Testperson", loaded.Name);
        Assert.Equal("Anna", loaded.Dictionary.Resolve("PERSON_1_FIRST"));
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        using (Vault.Create(_dir, "correct horse battery", Iterations)) { }
        Assert.Throws<UnauthorizedAccessException>(() => Vault.Open(_dir, "wrong password!"));
    }

    [Fact]
    public void Nothing_sensitive_is_stored_in_plaintext()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        vault.Save(SampleCase());
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var secret in new[] { "Müller", "Anna", "Muster-Testperson", "PERSON_1" })
            {
                Assert.False(
                    bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0,
                    $"{secret} found in plaintext in {Path.GetFileName(file)}"
                );
            }
        }

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Tampering_is_detected()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);
        var path = Directory.EnumerateFiles(Path.Combine(_dir, "cases")).Single();
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<CryptographicException>(() => vault.Load(c.Id));
        Assert.Equal(1, vault.LoadAll().Unreadable);
    }

    [Fact]
    public void Swapping_case_files_is_detected()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var a = SampleCase();
        var b = SampleCase();
        vault.Save(a);
        vault.Save(b);
        var casesDir = Path.Combine(_dir, "cases");
        File.Copy(
            Path.Combine(casesDir, a.Id + ".case"),
            Path.Combine(casesDir, b.Id + ".case"),
            overwrite: true
        );
        Assert.ThrowsAny<CryptographicException>(() => vault.Load(b.Id));
    }

    [Fact]
    public void Delete_removes_the_case()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);
        vault.Delete(c.Id);
        Assert.Empty(vault.LoadAll().Cases);
    }

    [Fact]
    public void Short_password_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Vault.Create(_dir, "short", Iterations));

    [Fact]
    public void Invalid_case_id_is_rejected()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        Assert.Throws<ArgumentException>(() => vault.Load("../etc/passwd"));
    }
}
