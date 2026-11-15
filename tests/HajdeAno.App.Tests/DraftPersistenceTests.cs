using System.Text;
using Avalonia;
using Avalonia.Headless;
using HajdeAno.App.Services;
using HajdeAno.App.ViewModels;
using HajdeAno.App.Views;
using HajdeAno.Core.Llm;

// Avalonia's platform is global per process: run the headless sessions one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HajdeAno.App.Tests;

/// <summary>
/// Builds the real application on the headless platform, with real text layout (Skia),
/// which is needed to select text with the mouse. All tests use the same builder.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>File dialog that "opens" a fixed file; clipboard is ignored.</summary>
internal sealed class FakeUi(string fileToOpen) : IUiServices
{
    /// <summary>What the next "Save as" dialog returns.</summary>
    public string? SaveAsPath { get; set; }

    public int SaveDialogs { get; private set; }

    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

    public Task<string?> PickTextFileAsync() => Task.FromResult<string?>(fileToOpen);

    public Task<string?> PickSaveTextFileAsync(string suggestedName)
    {
        SaveDialogs++;
        return Task.FromResult(SaveAsPath);
    }

    public Task<string?> PickModelFileAsync() => Task.FromResult<string?>(null);
}

/// <summary>
/// End-to-end: open a file in the program, close the program, start it again and unlock
/// the vault: the text must be back, and must never be on disk in plaintext.
/// </summary>
public sealed class DraftPersistenceTests : IDisposable
{
    private const string Password = "synthetic password 123";

    // Fictional text only.
    private const string SyntheticText =
        "Zora Blumenthal aus Olten bestreitet die Rechnung der Velowerkstatt Speiche GmbH über CHF 380.00.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "hajde-ano-e2e-" + Guid.NewGuid().ToString("N")
    );

    private string DataDir => Path.Combine(_root, "data");

    private string InputFile => Path.Combine(_root, "brief.txt");

    public DraftPersistenceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(InputFile, SyntheticText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Start the program: window, view models and services on a data directory.</summary>
    private FakeUi _ui = null!;

    private (MainWindow Window, MainWindowViewModel Main, AppServices Services) Start()
    {
        var services = new AppServices(DataDir);
        var window = new MainWindow();
        _ui = new FakeUi(InputFile);
        var main = new MainWindowViewModel(services, _ui);
        window.DataContext = main;
        window.Show();
        return (window, main, services);
    }

    private static async Task<WorkspaceViewModel> UnlockAsync(MainWindowViewModel main)
    {
        var unlock = Assert.IsType<UnlockViewModel>(main.Current);
        unlock.Password = Password;
        unlock.ConfirmPassword = Password;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.Null(unlock.Error);
        return Assert.IsType<WorkspaceViewModel>(main.Current);
    }

    /// <summary>Close the program like the user does: close the window, then exit.</summary>
    private static void Close(MainWindow window, AppServices services)
    {
        window.Close();
        services.Dispose();
    }

    [Fact]
    public async Task Opened_file_is_shown_again_after_restarting_the_program()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                // First run: create the vault and a case, open the file, close at once
                // (before the autosave timer fires).
                var (window, main, services) = Start();
                var workspace = await UnlockAsync(main);
                workspace.NewCaseName = "Synthetischer Fall";
                workspace.CreateCaseCommand.Execute(null);
                await workspace.Input.OpenFileCommand.ExecuteAsync(null);
                Assert.Equal(SyntheticText, workspace.Input.Text);
                Close(window, services);

                // Second run: unlock again; the case is selected and the text is back.
                (window, main, services) = Start();
                workspace = await UnlockAsync(main);
                Assert.Equal("Synthetischer Fall", workspace.SelectedCase?.Name);
                Assert.Equal(SyntheticText, workspace.Input.Text);
                Assert.Equal("Restored from the encrypted case.", workspace.Input.DraftStatus);
                Close(window, services);
                return true;
            },
            CancellationToken.None
        );

        AssertNoPlaintextOnDisk();
    }

    [Fact]
    public async Task Each_case_keeps_its_own_text()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var (window, main, services) = Start();
                var workspace = await UnlockAsync(main);

                workspace.NewCaseName = "Fall A";
                workspace.CreateCaseCommand.Execute(null);
                await workspace.Input.OpenFileCommand.ExecuteAsync(null);
                var caseA = workspace.SelectedCase!;

                workspace.NewCaseName = "Fall B";
                workspace.CreateCaseCommand.Execute(null);
                Assert.Equal("Fall B", workspace.SelectedCase?.Name);
                Assert.Equal("", workspace.Input.Text);
                workspace.Input.Text = "Nur für Fall B.";

                workspace.SelectedCase = workspace.Cases.Single(c => c.File.Id == caseA.File.Id);
                Assert.Equal(SyntheticText, workspace.Input.Text);

                workspace.SelectedCase = workspace.Cases.Single(c => c.Name == "Fall B");
                Assert.Equal("Nur für Fall B.", workspace.Input.Text);

                // Locking saves the draft as well.
                workspace.Input.Text = "Geändert vor dem Sperren.";
                main.Lock();
                workspace = await UnlockAsync(main);
                workspace.SelectedCase = workspace.Cases.Single(c => c.Name == "Fall B");
                Assert.Equal("Geändert vor dem Sperren.", workspace.Input.Text);

                Close(window, services);
                return true;
            },
            CancellationToken.None
        );

        AssertNoPlaintextOnDisk();
    }

    [Fact]
    public async Task Deleting_a_case_deletes_its_text()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var (window, main, services) = Start();
                var workspace = await UnlockAsync(main);
                workspace.NewCaseName = "Zu löschen";
                workspace.CreateCaseCommand.Execute(null);
                await workspace.Input.OpenFileCommand.ExecuteAsync(null);

                // The first click asks for confirmation, the second deletes.
                workspace.DeleteCaseCommand.Execute(null);
                workspace.DeleteCaseCommand.Execute(null);
                Assert.Empty(workspace.Cases);
                Assert.Equal("", workspace.Input.Text);
                Close(window, services);
                return true;
            },
            CancellationToken.None
        );

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(DataDir, "cases")));
    }

    [Fact]
    public async Task New_and_open_ask_before_replacing_text_and_save_as_exports()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var (window, main, services) = Start();
                var workspace = await UnlockAsync(main);
                workspace.NewCaseName = "Dateien";
                workspace.CreateCaseCommand.Execute(null);
                var input = workspace.Input;

                // Empty text: open without asking.
                await input.OpenFileCommand.ExecuteAsync(null);
                Assert.False(input.IsConfirming);
                Assert.Equal(SyntheticText, input.Text);

                // Open again: asks first; cancelling keeps the text.
                input.Text = "Bearbeitet.";
                await input.OpenFileCommand.ExecuteAsync(null);
                Assert.True(input.IsConfirming);
                input.CancelConfirmCommand.Execute(null);
                Assert.Equal("Bearbeitet.", input.Text);

                // New: asks first; confirming empties the text.
                await input.NewCommand.ExecuteAsync(null);
                Assert.True(input.IsConfirming);
                Assert.Equal("Bearbeitet.", input.Text);
                await input.ConfirmCommand.ExecuteAsync(null);
                Assert.Equal("", input.Text);

                // Save as exports the text; cancelling the dialog writes nothing.
                input.Text = "Exportierte Fassung.";
                await input.SaveAsCommand.ExecuteAsync(null);
                Assert.Equal(1, _ui.SaveDialogs);
                var target = Path.Combine(_root, "export.txt");
                _ui.SaveAsPath = target;
                await input.SaveAsCommand.ExecuteAsync(null);
                Assert.Equal(2, _ui.SaveDialogs);
                Assert.Equal("Exportierte Fassung.", File.ReadAllText(target));
                Close(window, services);
                return true;
            },
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Autosave_only_writes_when_the_text_changed()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var (window, main, services) = Start();
                var workspace = await UnlockAsync(main);
                workspace.NewCaseName = "Autosave";
                workspace.CreateCaseCommand.Execute(null);
                var input = workspace.Input;
                var caseFile = workspace.CurrentCase!;

                input.Text = "Erste Fassung.";
                input.FlushDraft();
                var revision = caseFile.Revision;

                // Typing and undoing leaves the text unchanged: nothing is written.
                input.Text = "Erste Fassung!";
                input.Text = "Erste Fassung.";
                input.FlushDraft();
                Assert.Equal(revision, caseFile.Revision);

                input.Text = "Zweite Fassung.";
                input.FlushDraft();
                Assert.Equal(revision + 1, caseFile.Revision);
                Close(window, services);
                return true;
            },
            CancellationToken.None
        );
    }

    /// <summary>Extractor which waits until the test lets it finish.</summary>
    private sealed class GatedExtractor : IEntityExtractor
    {
        public TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxChunkChars => 4000;

        public async Task<string> ExtractRawAsync(
            string text,
            IProgress<InferenceProgress>? progress,
            CancellationToken cancellationToken
        )
        {
            await Gate.Task.WaitAsync(cancellationToken);
            return text.Contains("Zora Blumenthal", StringComparison.Ordinal)
                ? """[{"text": "Zora Blumenthal", "type": "PERSON"}]"""
                : "[]";
        }
    }

    [Fact]
    public async Task Detection_result_never_goes_into_another_case()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var (window, main, services) = Start();
                var workspace = await UnlockAsync(main);
                workspace.NewCaseName = "Fall B";
                workspace.CreateCaseCommand.Execute(null);
                workspace.NewCaseName = "Fall A";
                workspace.CreateCaseCommand.Execute(null);
                await workspace.Input.OpenFileCommand.ExecuteAsync(null);

                // While the detection runs, the case cannot be switched, deleted or locked.
                var extractor = new GatedExtractor();
                services.ExtractorOverride = extractor;
                var detection = workspace.Input.DetectCommand.ExecuteAsync(null);
                Assert.True(workspace.IsDetecting);
                Assert.False(workspace.CanSwitchCase);
                Assert.False(workspace.DeleteCaseCommand.CanExecute(null));
                Assert.False(workspace.LockCommand.CanExecute(null));
                workspace.NewCaseName = "Fall C";
                Assert.False(workspace.CreateCaseCommand.CanExecute(null));

                // Even if the case changes anyway, the result is discarded.
                workspace.SelectedCase = workspace.Cases.Single(c => c.Name == "Fall B");
                extractor.Gate.SetResult();
                await detection;
                Assert.False(workspace.IsDetecting);
                Assert.Contains("discarded", workspace.Input.Error, StringComparison.Ordinal);
                Assert.False(workspace.Review.HasDocument);
                Assert.Empty(workspace.CurrentCase!.Dictionary.Entities);

                // A normal detection goes to the review of the case it ran on.
                workspace.SelectedCase = workspace.Cases.Single(c => c.Name == "Fall A");
                extractor = new GatedExtractor();
                services.ExtractorOverride = extractor;
                extractor.Gate.SetResult();
                await workspace.Input.DetectCommand.ExecuteAsync(null);
                Assert.Null(workspace.Input.Error);
                Assert.True(workspace.Review.HasDocument);
                Close(window, services);
                return true;
            },
            CancellationToken.None
        );
    }

    private void AssertNoPlaintextOnDisk()
    {
        foreach (var file in Directory.EnumerateFiles(DataDir, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (
                var secret in new[] { "Blumenthal", "Velowerkstatt", "Fall B", "Synthetischer" }
            )
            {
                Assert.False(
                    bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0,
                    $"\"{secret}\" found in plaintext in {Path.GetFileName(file)}"
                );
            }
        }
    }
}
