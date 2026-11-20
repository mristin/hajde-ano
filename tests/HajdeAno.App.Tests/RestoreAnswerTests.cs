using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HajdeAno.App.Services;
using HajdeAno.App.ViewModels;
using HajdeAno.App.Views;
using HajdeAno.Core;

namespace HajdeAno.App.Tests;

/// <summary>
/// End-to-end: restore an answer from the online service given as a Word document (keeping
/// its formatting) or as a text file.
/// </summary>
public sealed class RestoreAnswerTests : IDisposable
{
    private const string Ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "hajde-ano-e2e-" + Guid.NewGuid().ToString("N")
    );

    public RestoreAnswerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A minimal Word document, as an online service would produce it.</summary>
    private static void WriteDocx(string path, string bodyXml)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        Add(
            "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>"""
        );
        Add(
            "word/document.xml",
            $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="{Ns}"><w:body>{bodyXml}</w:body></w:document>"""
        );
    }

    private static string DocumentXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Word_and_text_answers_are_restored()
    {
        // A long file name, as downloads from online services often have.
        var answer = Path.Combine(
            _root,
            "antwort-auf-die-anfrage-zur-rechnung-der-velowerkstatt-vom-maerz.docx"
        );
        WriteDocx(
            answer,
            """
            <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Antwort an [PERSON_1]</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">Frau [PERSON_</w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>1_LAST] und [PERSON_9].</w:t></w:r></w:p>
            """
        );
        var textAnswer = Path.Combine(_root, "antwort.md");
        File.WriteAllText(textAnswer, "Liebe [PERSON_1_FIRST], danke.");

        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var services = new AppServices(Path.Combine(_root, "data"));
                var ui = new FakeUi("");
                var window = new MainWindow { Width = 1100, Height = 800 };
                var main = new MainWindowViewModel(services, ui);
                window.DataContext = main;
                window.Show();
                var workspace = await TestSession.OpenAsync(main, Path.Combine(_root, "session"));
                workspace.NewCaseName = "Antwort";
                workspace.CreateCaseCommand.Execute(null);
                workspace.CurrentCase!.Dictionary.Assign("Zora Blumenthal", Category.Person);
                workspace.SaveCurrentCase();
                var restore = workspace.Restore;

                // Word: open, preview, save a restored copy.
                ui.AnswerPath = answer;
                await restore.OpenAnswerCommand.ExecuteAsync(null);
                Assert.True(restore.IsWord);
                Assert.Equal(Path.GetFileName(answer), restore.WordFileName);

                // The file name must not run into the label of the restored text.
                workspace.SelectedTab = WorkspaceViewModel.RestoreTab;
                for (var i = 0; i < 3; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                TextBlock Visible(string startsWith) =>
                    window
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Single(t =>
                            t.IsEffectivelyVisible
                            && (t.Text ?? "").StartsWith(startsWith, StringComparison.Ordinal)
                        );
                Rect InWindow(Control c) =>
                    new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
                var fileLabel = Visible("Word document: antwort-auf");
                var restoredLabel = Visible("Restored text (");
                Assert.False(
                    InWindow(fileLabel).Intersects(InWindow(restoredLabel)),
                    $"{InWindow(fileLabel)} overlaps {InWindow(restoredLabel)}"
                );
                Assert.Contains(
                    "Antwort an Zora Blumenthal",
                    restore.Output,
                    StringComparison.Ordinal
                );
                Assert.Contains(
                    "Frau Blumenthal und [PERSON_9].",
                    restore.Output,
                    StringComparison.Ordinal
                );
                Assert.Single(restore.Issues); // [PERSON_9] is unknown

                var restored = Path.Combine(_root, "antwort-restored.docx");
                ui.SaveWordPath = restored;
                Assert.True(restore.SaveWordCommand.CanExecute(null));
                await restore.SaveWordCommand.ExecuteAsync(null);
                var xml = DocumentXml(restored);
                Assert.Contains("Antwort an Zora Blumenthal", xml, StringComparison.Ordinal);
                Assert.Contains("Frau Blumenthal", xml, StringComparison.Ordinal);
                Assert.Contains(
                    "<w:i />",
                    xml.Replace("<w:i/>", "<w:i />", StringComparison.Ordinal),
                    StringComparison.Ordinal
                );
                Assert.DoesNotContain("[PERSON_1", xml, StringComparison.Ordinal);

                // Editing the text sets the Word document aside.
                restore.Input += " Nachtrag.";
                Assert.False(restore.IsWord);
                Assert.False(restore.SaveWordCommand.CanExecute(null));

                // Text: open and restore right away.
                ui.AnswerPath = textAnswer;
                await restore.OpenAnswerCommand.ExecuteAsync(null);
                Assert.False(restore.IsWord);
                Assert.Equal("Liebe Zora, danke.", restore.Output);

                // A damaged file is reported, not thrown.
                var broken = Path.Combine(_root, "kaputt.docx");
                File.WriteAllText(broken, "not a zip");
                ui.AnswerPath = broken;
                await restore.OpenAnswerCommand.ExecuteAsync(null);
                Assert.Contains("Could not read", restore.Message, StringComparison.Ordinal);

                window.Close();
                services.Dispose();
                return true;
            },
            CancellationToken.None
        );
    }
}
