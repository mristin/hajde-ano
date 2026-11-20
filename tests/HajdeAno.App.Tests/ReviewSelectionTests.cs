using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HajdeAno.App.Controls;
using HajdeAno.App.Services;
using HajdeAno.App.ViewModels;
using HajdeAno.App.Views;
using HajdeAno.Core.Llm;

namespace HajdeAno.App.Tests;

/// <summary>
/// End-to-end: on the Review screen, select a missed surname in the original text with the
/// mouse and click "Anonymize selection", exactly like a user.
/// </summary>
public sealed class ReviewSelectionTests : IDisposable
{
    // Fictional text only. The model "finds" nothing, so the surname must be added by hand.
    private const string SyntheticText =
        "Sehr geehrte Frau Blumenthal, wir bestätigen den Eingang Ihres Schreibens. Frau Blumenthal erhält die Antwort bis Freitag.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "hajde-ano-e2e-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NothingFound : IEntityExtractor
    {
        public int MaxChunkChars => 4000;

        public Task<string> ExtractRawAsync(
            string text,
            IProgress<InferenceProgress>? progress,
            CancellationToken cancellationToken
        ) => Task.FromResult("[]");
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    /// <summary>Window coordinates of the left edge of the character at the index.</summary>
    private static Point CharacterPoint(HighlightedTextBlock block, Window window, int index)
    {
        var rect = block.TextLayout.HitTestTextPosition(index);
        var local = new Point(
            block.Padding.Left + rect.X + 0.5,
            block.Padding.Top + rect.Y + rect.Height / 2
        );
        return block.TranslatePoint(local, window)
            ?? throw new InvalidOperationException("The text block is not in the window");
    }

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
            window
        ) ?? throw new InvalidOperationException("The control is not in the window");
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Settle(window);
    }

    [Fact]
    public async Task Selecting_a_surname_and_clicking_anonymize_selection_replaces_it()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var services = new AppServices(Path.Combine(_root, "data"))
                {
                    ExtractorOverride = new NothingFound(),
                };
                var window = new MainWindow { Width = 1400, Height = 900 };
                var main = new MainWindowViewModel(services, new FakeUi(""));
                window.DataContext = main;
                window.Show();

                var workspace = TestSession.Open(main, Path.Combine(_root, "session"));
                workspace.NewCaseName = "Auswahl";
                workspace.CreateCaseCommand.Execute(null);
                workspace.Input.Text = SyntheticText;
                await workspace.Input.DetectCommand.ExecuteAsync(null);
                Assert.Equal(WorkspaceViewModel.ReviewTab, workspace.SelectedTab);
                Settle(window);

                var review = workspace.Review;
                Assert.Contains("Blumenthal", review.AnonymizedText, StringComparison.Ordinal);

                // Drag over the first "Blumenthal" in the original (the left text block).
                var original = window
                    .GetVisualDescendants()
                    .OfType<HighlightedTextBlock>()
                    .First(b => b.IsEffectivelyVisible);
                var start = SyntheticText.IndexOf("Blumenthal", StringComparison.Ordinal);
                var end = start + "Blumenthal".Length;
                var from = CharacterPoint(original, window, start);
                var to = CharacterPoint(original, window, end);
                window.MouseMove(from);
                window.MouseDown(from, MouseButton.Left);
                window.MouseMove(to, RawInputModifiers.LeftMouseButton);
                window.MouseUp(to, MouseButton.Left);
                Settle(window);
                Assert.Equal("Blumenthal", original.SelectedText);

                // Click the button with the mouse, like the user.
                var button = window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Single(b =>
                        b.IsEffectivelyVisible && b.Content as string == "Anonymize selection"
                    );
                Click(window, button);

                Assert.Null(review.Message);
                Assert.DoesNotContain(
                    "Blumenthal",
                    review.AnonymizedText,
                    StringComparison.Ordinal
                );
                Assert.Contains("[PERSON_1", review.AnonymizedText, StringComparison.Ordinal);

                window.Close();
                services.Dispose();
                return true;
            },
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Clicking_the_disabled_copy_button_explains_why()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var services = new AppServices(Path.Combine(_root, "data"))
                {
                    ExtractorOverride = new NothingFound(),
                };
                var window = new MainWindow { Width = 1400, Height = 900 };
                var main = new MainWindowViewModel(services, new FakeUi(""));
                window.DataContext = main;
                window.Show();

                var workspace = TestSession.Open(main, Path.Combine(_root, "session"));
                workspace.NewCaseName = "Kopieren";
                workspace.CreateCaseCommand.Execute(null);
                var review = workspace.Review;
                Button CopyButton() =>
                    window
                        .GetVisualDescendants()
                        .OfType<Button>()
                        .Single(b =>
                            b.IsEffectivelyVisible && b.Content as string == "Copy anonymized text"
                        );

                // Nothing detected yet.
                workspace.SelectedTab = WorkspaceViewModel.ReviewTab;
                Settle(window);
                Assert.False(CopyButton().IsEffectivelyEnabled);
                Click(window, CopyButton());
                Assert.Contains("Detect", review.CopyHint, StringComparison.Ordinal);

                // Detected, but the review is not confirmed yet.
                workspace.Input.Text = SyntheticText;
                await workspace.Input.DetectCommand.ExecuteAsync(null);
                Settle(window);
                Assert.Null(review.CopyHint);
                Assert.False(CopyButton().IsEffectivelyEnabled);
                Click(window, CopyButton());
                Assert.Contains("tick", review.CopyHint, StringComparison.OrdinalIgnoreCase);

                // Once confirmed, the button works and the hint disappears.
                review.Confirmed = true;
                Settle(window);
                Assert.Null(review.CopyHint);
                Assert.True(CopyButton().IsEffectivelyEnabled);
                Click(window, CopyButton());
                Assert.Contains("copied", review.Message, StringComparison.Ordinal);

                window.Close();
                services.Dispose();
                return true;
            },
            CancellationToken.None
        );
    }
}
