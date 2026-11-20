using HajdeAno.Core.Storage;

namespace HajdeAno.Core.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "hajde-ano-test-" + Guid.NewGuid().ToString("N")
    );

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
        var session = Session.Create(_dir);
        var c = SampleCase();
        session.Save(c);

        var reopened = Session.Open(_dir);
        var (cases, unreadable) = reopened.LoadAll();
        Assert.Equal(0, unreadable);
        var loaded = Assert.Single(cases);
        Assert.Equal("Fall Muster-Testperson", loaded.Name);
        Assert.Equal("Anna", loaded.Dictionary.Resolve("PERSON_1_FIRST"));
        Assert.Equal("Entwurf: Zora Blumenthal schreibt an die Vermieterin.", loaded.Draft);
    }

    [Fact]
    public void Layout_is_a_marker_and_one_json_file_per_case()
    {
        var session = Session.Create(_dir);
        var c = SampleCase();
        session.Save(c);

        Assert.True(Session.IsSession(_dir));
        Assert.Equal(
            new[] { "cases", Session.MarkerFileName },
            Directory
                .EnumerateFileSystemEntries(_dir)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            c.Id + ".json",
            Path.GetFileName(Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "cases"))))
        );
    }

    [Fact]
    public void Name_is_the_folder_name() =>
        Assert.Equal(Path.GetFileName(_dir), Session.Create(_dir).Name);

    [Fact]
    public void A_new_session_needs_an_empty_folder()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "x");
        Assert.Throws<SessionException>(() => Session.Create(_dir));
    }

    [Fact]
    public void A_session_cannot_be_created_twice()
    {
        Session.Create(_dir);
        Assert.Throws<InvalidOperationException>(() => Session.Create(_dir));
    }

    [Fact]
    public void A_folder_without_a_session_is_rejected()
    {
        Directory.CreateDirectory(_dir);
        Assert.False(Session.IsSession(_dir));
        Assert.Throws<SessionException>(() => Session.Open(_dir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"version": 99}""")]
    public void A_damaged_session_file_is_reported(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, Session.MarkerFileName), content);
        Assert.Throws<SessionException>(() => Session.Open(_dir));
    }

    [Fact]
    public void Damaged_case_files_are_skipped_and_counted()
    {
        var session = Session.Create(_dir);
        var good = SampleCase();
        session.Save(good);
        var bad = new CaseFile { Name = "Kaputt" };
        session.Save(bad);
        File.WriteAllText(Path.Combine(_dir, "cases", bad.Id + ".json"), "{ not json");

        var (cases, unreadable) = session.LoadAll();
        Assert.Equal(good.Id, Assert.Single(cases).Id);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void Saving_updates_revision_and_draft()
    {
        var session = Session.Create(_dir);
        var c = SampleCase();
        session.Save(c);
        Assert.Equal(1, c.Revision);
        c.Draft = "Neuer Entwurf";
        session.Save(c);
        Assert.Equal(2, c.Revision);
        Assert.Equal("Neuer Entwurf", session.Load(c.Id).Draft);
    }

    [Fact]
    public void Delete_removes_the_case()
    {
        var session = Session.Create(_dir);
        var c = SampleCase();
        session.Save(c);
        Assert.True(session.Contains(c.Id));
        session.Delete(c.Id);
        Assert.False(session.Contains(c.Id));
        Assert.Empty(session.LoadAll().Cases);
        Assert.Throws<FileNotFoundException>(() => session.Load(c.Id));
    }

    [Fact]
    public void Leftover_temporary_files_are_removed_on_open()
    {
        Session.Create(_dir);
        var tmp = Path.Combine(_dir, "cases", "abc.json.tmp");
        File.WriteAllText(tmp, "half");
        Session.Open(_dir);
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Case_ids_cannot_escape_the_folder()
    {
        var session = Session.Create(_dir);
        Assert.Throws<ArgumentException>(() => session.Load("../etc/passwd"));
        Assert.Throws<ArgumentException>(() => session.Save(new CaseFile { Id = "../x" }));
    }
}
