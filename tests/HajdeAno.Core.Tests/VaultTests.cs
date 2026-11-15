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
        var c = new CaseFile
        {
            Name = "Fall Muster-Testperson",
            Draft = "Entwurf: Zora Blumenthal schreibt an die Vermieterin.",
        };
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
        Assert.Equal("Entwurf: Zora Blumenthal schreibt an die Vermieterin.", loaded.Draft);
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
            foreach (
                var secret in new[]
                {
                    "Müller",
                    "Anna",
                    "Muster-Testperson",
                    "PERSON_1",
                    "Blumenthal",
                    "Vermieterin",
                }
            )
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

    private string CasesDir => Path.Combine(_dir, "cases");

    private string SingleCaseFile() => Directory.EnumerateFiles(CasesDir).Single();

    [Fact]
    public void Swapping_case_files_is_detected()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var a = SampleCase();
        vault.Save(a);
        var pathA = SingleCaseFile();
        var b = SampleCase();
        vault.Save(b);
        var pathB = Directory.EnumerateFiles(CasesDir).Single(p => p != pathA);
        File.Copy(pathA, pathB, overwrite: true);
        Assert.ThrowsAny<CryptographicException>(() => vault.Load(b.Id));
    }

    [Fact]
    public void File_names_reveal_neither_ids_nor_names()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);
        var name = Path.GetFileName(SingleCaseFile());
        Assert.DoesNotContain(c.Id, name, StringComparison.Ordinal);
        Assert.Matches("^[A-Za-z0-9_-]{43}\\.case$", name);
        Assert.Equal(
            new[] { "cases", "index", "vault.json" },
            Directory
                .EnumerateFileSystemEntries(_dir)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Restoring_an_older_copy_of_a_case_is_detected()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);
        var old = File.ReadAllBytes(SingleCaseFile());
        c.Draft = "Neuer Entwurf";
        vault.Save(c);
        File.WriteAllBytes(SingleCaseFile(), old);

        Assert.Throws<Vault.OutdatedCaseException>(() => vault.Load(c.Id));
        using var reopened = Vault.Open(_dir, "correct horse battery");
        var loaded = reopened.LoadAll();
        Assert.Empty(loaded.Cases);
        Assert.Equal(1, loaded.Outdated);
    }

    [Fact]
    public void A_copy_of_a_deleted_case_cannot_be_decrypted()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);
        var path = SingleCaseFile();
        var copy = File.ReadAllBytes(path);
        vault.Delete(c.Id);
        Assert.False(File.Exists(path));

        // Put the old ciphertext back (as if recovered from the disk): it is not loaded,
        // and reopening the vault shreds it.
        File.WriteAllBytes(path, copy);
        Assert.Empty(vault.LoadAll().Cases);
        Assert.Throws<FileNotFoundException>(() => vault.Load(c.Id));
        using var reopened = Vault.Open(_dir, "correct horse battery");
        Assert.Empty(reopened.LoadAll().Cases);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Leftover_temporary_files_are_removed_on_open()
    {
        using (var vault = Vault.Create(_dir, "correct horse battery", Iterations))
        {
            vault.Save(SampleCase());
        }

        File.WriteAllBytes(Path.Combine(CasesDir, "x.case.tmp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "index.tmp"), [1, 2, 3]);
        using var reopened = Vault.Open(_dir, "correct horse battery");
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories));
        Assert.Single(reopened.LoadAll().Cases);
    }

    [Fact]
    public void Saving_writes_a_new_file_instead_of_overwriting_in_place()
    {
        using var vault = Vault.Create(_dir, "correct horse battery", Iterations);
        var c = SampleCase();
        vault.Save(c);
        var path = SingleCaseFile();
        using var handle = File.OpenHandle(path);
        var before = File.ReadAllBytes(path);
        c.Draft = "Neuer Entwurf";
        vault.Save(c);

        // The handle still sees the old (complete) file: the new one was renamed over it.
        var buffer = new byte[before.Length];
        RandomAccess.Read(handle, buffer, 0);
        Assert.Equal(before, buffer);
        Assert.Equal("Neuer Entwurf", vault.Load(c.Id).Draft);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData(
        "{\"version\": 2, \"kdf\": \"PBKDF2-HMAC-SHA256\", \"iterations\": 2000000000, \"salt\": \"AAAAAAAAAAAAAAAAAAAAAA==\", \"check\": \"\"}"
    )]
    [InlineData(
        "{\"version\": 2, \"kdf\": \"PBKDF2-HMAC-SHA256\", \"iterations\": 600000, \"salt\": \"%%%\", \"check\": \"\"}"
    )]
    [InlineData(
        "{\"version\": 9, \"kdf\": \"PBKDF2-HMAC-SHA256\", \"iterations\": 600000, \"salt\": \"AAAAAAAAAAAAAAAAAAAAAA==\", \"check\": \"\"}"
    )]
    [InlineData(
        "{\"version\": 2, \"kdf\": \"MD5\", \"iterations\": 600000, \"salt\": \"AAAAAAAAAAAAAAAAAAAAAA==\", \"check\": \"\"}"
    )]
    [InlineData("null")]
    public void A_damaged_vault_file_is_reported_and_does_not_hang(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "vault.json"), content);
        Assert.Throws<VaultCorruptException>(() => Vault.Open(_dir, "correct horse battery"));
    }

    [Fact]
    public void A_damaged_index_is_reported()
    {
        using (Vault.Create(_dir, "correct horse battery", Iterations)) { }
        var index = Path.Combine(_dir, "index");
        var bytes = File.ReadAllBytes(index);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(index, bytes);
        Assert.Throws<VaultCorruptException>(() => Vault.Open(_dir, "correct horse battery"));
    }

    [Fact]
    public void Version_1_vaults_are_migrated()
    {
        // Build a version 1 vault by hand: cases encrypted with the password key, named by id.
        Directory.CreateDirectory(CasesDir);
        var salt = Crypto.RandomBytes(Crypto.SaltSize);
        var key = Crypto.DeriveKey("correct horse battery", salt, Iterations);
        File.WriteAllText(
            Path.Combine(_dir, "vault.json"),
            $$"""{"version":1,"kdf":"PBKDF2-HMAC-SHA256","iterations":{{Iterations}},"salt":"{{Convert.ToBase64String(salt)}}","check":"{{Convert.ToBase64String(Crypto.Encrypt(key, "hajde-ano vault verifier v1"u8.ToArray()))}}"}"""
        );
        var c = SampleCase();
        var json =
            $$"""{"id":"{{c.Id}}","name":"Fall Muster-Testperson","draft":"Alter Entwurf"}""";
        var oldPath = Path.Combine(CasesDir, c.Id + ".case");
        File.WriteAllBytes(
            oldPath,
            Crypto.Encrypt(key, Encoding.UTF8.GetBytes(json), Encoding.ASCII.GetBytes(c.Id))
        );

        using (var vault = Vault.Open(_dir, "correct horse battery"))
        {
            var loaded = Assert.Single(vault.LoadAll().Cases);
            Assert.Equal("Alter Entwurf", loaded.Draft);
        }

        Assert.False(File.Exists(oldPath));
        Assert.Contains("\"version\":2", File.ReadAllText(Path.Combine(_dir, "vault.json")));
        using var again = Vault.Open(_dir, "correct horse battery");
        Assert.Equal("Fall Muster-Testperson", Assert.Single(again.LoadAll().Cases).Name);
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
        Assert.Throws<ArgumentException>(() => vault.Save(new CaseFile { Id = "../x" }));
    }
}
