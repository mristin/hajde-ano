using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using HajdeAno.App.Services;
using HajdeAno.App.ViewModels;
using HajdeAno.App.Views;
using HajdeAno.Core;
using HajdeAno.Core.Llm;
using HajdeAno.Core.Storage;

namespace HajdeAno.Screenshots;

/// <summary>Stand-in for the model that "detects" the annotated synthetic entities.</summary>
internal sealed class OracleExtractor(IReadOnlyList<ExpectedEntity> expected) : IEntityExtractor
{
    public int MaxChunkChars => 4000;

    public Task<string> ExtractRawAsync(
        string text,
        IProgress<InferenceProgress>? progress,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            JsonSerializer.Serialize(
                expected
                    .Where(e => text.Contains(e.Text, StringComparison.Ordinal))
                    .Select(e => new { text = e.Text, type = e.Type })
            )
        );
}

internal sealed class NoUi : IUiServices
{
    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

    public Task<string?> PickTextFileAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickModelFileAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickSaveTextFileAsync(string suggestedName) =>
        Task.FromResult<string?>(null);

    public Task<string?> PickAnswerFileAsync()
    {
        throw new NotImplementedException();
    }

    public Task<string?> PickSaveWordFileAsync(string suggestedName)
    {
        throw new NotImplementedException();
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
}

internal static class Program
{
    private const int Width = 1280;
    private const int Height = 780;

    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "screenshots";
        var casePath =
            args.Length > 1
                ? args[1]
                : Path.Combine("tests", "data", "cases", "06-musikschule.json");
        Directory.CreateDirectory(outDir);

        var dataDir = Path.Combine(
            Path.GetTempPath(),
            "hajde-ano-screenshots-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            AppBuilder
                .Configure<App.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            // Run the scenario on the UI thread and keep the dispatcher loop alive meanwhile;
            // blocking on the task would deadlock the awaits inside the view models.
            using var done = new CancellationTokenSource();
            Exception? error = null;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Run(outDir, casePath, dataDir);
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    done.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            if (error is not null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            return 0;
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Capture(MainWindow window, string path)
    {
        Pump();
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No frame rendered");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine($"Wrote {path}");
    }

    private static async Task Run(string outDir, string casePath, string dataDir)
    {
        var synthetic = SyntheticCase.FromJson(await File.ReadAllTextAsync(casePath));
        var services = new AppServices(dataDir)
        {
            ExtractorOverride = new OracleExtractor(synthetic.Expected),
        };
        services.Settings.ModelPath = "models/Qwen3-8B-Q4_K_M.gguf";

        var window = new MainWindow { Width = Width, Height = Height };
        var main = new MainWindowViewModel(services, new NoUi());
        window.DataContext = main;
        window.Show();

        // 1. Session picker with a few (synthetic) recent sessions.
        var sessionsRoot = Path.Combine(dataDir, "sessions");
        foreach (var name in new[] { "Mietrecht 2026", "Versicherungen", "Familie" })
        {
            Session.Create(Path.Combine(sessionsRoot, name));
        }

        foreach (var name in new[] { "Familie", "Versicherungen", "Mietrecht 2026" })
        {
            services.Settings.AddRecentSession(Path.Combine(sessionsRoot, name));
        }

        main.Current = new SessionPickerViewModel(services, new NoUi(), () => { });
        Capture(window, Path.Combine(outDir, "sessions.png"));

        services.Session = Session.Open(Path.Combine(sessionsRoot, "Mietrecht 2026"));
        var workspace = new WorkspaceViewModel(services, new NoUi(), main.CloseSession);
        main.Current = workspace;

        foreach (var name in new[] { "Beispiel – Velodiebstahl", "Muster – Musikschule" })
        {
            workspace.NewCaseName = name;
            workspace.CreateCaseCommand.Execute(null);
        }

        workspace.SelectedCase = workspace.Cases.First(c =>
            c.Name.Contains("Musikschule", StringComparison.Ordinal)
        );

        // 2. Input screen, with the confirmation shown when replacing existing text.
        workspace.Input.Text = synthetic.Documents[0];
        await workspace.Input.NewCommand.ExecuteAsync(null);
        Capture(window, Path.Combine(outDir, "input.png"));
        workspace.Input.CancelConfirmCommand.Execute(null);

        // 3. Review screen.
        await workspace.Input.DetectCommand.ExecuteAsync(null);
        Pump();
        foreach (var g in workspace.Review.Groups.Take(3))
        {
            g.ApproveCommand.Execute(null);
        }

        Capture(window, Path.Combine(outDir, "review.png"));

        // Commit the mappings so that restoration works.
        workspace.Review.ApproveAllCommand.Execute(null);
        workspace.Review.Confirmed = true;
        await workspace.Review.CopyCommand.ExecuteAsync(null);

        // 4. Restore screen with an answer from an "online service".
        workspace.Restore.Input =
            "Sehr geehrte Damen und Herren der [ORG_1]\n\n"
            + "Hiermit bestreite ich, [PERSON_1], Ihre Rechnung [CUSTOMER_ID_1] über [AMOUNT_1]. "
            + "Meine Tochter [PERSON_2_FIRST] hat von [PERSON_3] nur vier von zwölf Lektionen erhalten. "
            + "Ich habe bereits [AMOUNT_2] überwiesen und betrachte die Forderung damit als beglichen.\n\n"
            + "Freundliche Grüsse\n[PERSON_1]\n[ADDRESS_1]\n\nPS: Bitte antworten Sie an [EMAIL 1].";
        workspace.Restore.RestoreCommand.Execute(null);
        workspace.SelectedTab = WorkspaceViewModel.RestoreTab;
        Capture(window, Path.Combine(outDir, "restore.png"));

        // 5. Entities of the case.
        workspace.SelectedTab = WorkspaceViewModel.EntitiesTab;
        Capture(window, Path.Combine(outDir, "entities.png"));

        window.Close();
        services.Dispose();
    }
}
