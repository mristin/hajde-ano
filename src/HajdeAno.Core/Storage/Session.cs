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
/// A folder chosen by the user in which the cases are stored, unencrypted.
/// </summary>
/// <remarks>
/// <para>Layout of the session folder:</para>
/// <list type="bullet">
/// <item><c>hajde-ano-session.json</c>: marks the folder as a session (no private data).</item>
/// <item><c>cases/&lt;id&gt;.json</c>: one file per case.</item>
/// </list>
/// <para>Writes go to a temporary file which is then renamed, so a crash never leaves a
/// half-written file.</para>
/// </remarks>
public sealed class Session
{
    public const string MarkerFileName = "hajde-ano-session.json";
    private const string CasesDirName = "cases";
    private const string CaseExtension = ".json";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    private Session(string directory) => Directory = Path.GetFullPath(directory);

    /// <summary>Folder of the session.</summary>
    public string Directory { get; }

    /// <summary>Name shown to the user: the name of the folder.</summary>
    public string Name => NameOf(Directory);

    private string CasesDir => Path.Combine(Directory, CasesDirName);

    private sealed class Marker
    {
        public int Version { get; set; } = CurrentVersion;
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
    /// Create a new session in <paramref name="directory"/>, which must not exist yet or be
    /// empty (so that the cases do not get mixed with other files).
    /// </summary>
    public static Session Create(string directory)
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

        var session = new Session(directory);
        System.IO.Directory.CreateDirectory(session.CasesDir);
        AtomicWrite(
            Path.Combine(session.Directory, MarkerFileName),
            JsonSerializer.SerializeToUtf8Bytes(new Marker(), JsonOptions)
        );
        return session;
    }

    /// <summary>
    /// Open an existing session. Throws <see cref="SessionException"/> if the folder is not
    /// a session or was created by a newer version.
    /// </summary>
    public static Session Open(string directory)
    {
        var path = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(path))
        {
            throw new SessionException("The folder is not a Hajde Ano session");
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

        var session = new Session(directory);
        System.IO.Directory.CreateDirectory(session.CasesDir);
        session.CleanUp();
        return session;
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

    /// <summary>Whether the case is part of the session.</summary>
    public bool Contains(string id) => File.Exists(CasePath(id));

    /// <summary>Save the case (atomically).</summary>
    public void Save(CaseFile caseFile)
    {
        CheckId(caseFile.Id);
        caseFile.Modified = DateTimeOffset.UtcNow;
        caseFile.Revision++;
        AtomicWrite(
            CasePath(caseFile.Id),
            JsonSerializer.SerializeToUtf8Bytes(caseFile, JsonOptions)
        );
    }

    /// <summary>
    /// Load a case. Throws <see cref="FileNotFoundException"/> if it does not exist and
    /// <see cref="InvalidDataException"/> if the file is damaged.
    /// </summary>
    public CaseFile Load(string id)
    {
        var path = CasePath(id);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The case does not exist");
        }

        CaseFile? caseFile;
        try
        {
            caseFile = JsonSerializer.Deserialize<CaseFile>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Invalid case file", e);
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

    /// <summary>
    /// Write via a temporary file in the same directory, flush it to the disk and rename it
    /// over the target, so that a crash leaves either the old or the new file, never a mix.
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
}
