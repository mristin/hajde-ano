using Avalonia.Headless;
using HajdeAno.App.Services;
using HajdeAno.App.ViewModels;
using HajdeAno.App.Views;
using HajdeAno.Core.Storage;

namespace HajdeAno.App.Tests;

/// <summary>The start screen: new session, open session, recent sessions.</summary>
public sealed class SessionPickerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "hajde-ano-e2e-" + Guid.NewGuid().ToString("N")
    );

    public SessionPickerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DataDir => Path.Combine(_root, "data");

    private static SessionPickerViewModel Picker(MainWindowViewModel main) =>
        Assert.IsType<SessionPickerViewModel>(main.Current);

    [Fact]
    public async Task New_open_recent_and_close()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(
            async () =>
            {
                var services = new AppServices(DataDir);
                var ui = new FakeUi("");
                var window = new MainWindow();
                var main = new MainWindowViewModel(services, ui);
                window.DataContext = main;
                window.Show();
                Assert.False(Picker(main).HasRecent);
                Assert.Equal("Hajde Ano", main.Title);

                // Cancelling the dialog does nothing.
                await Picker(main).NewSessionCommand.ExecuteAsync(null);
                Assert.IsType<SessionPickerViewModel>(main.Current);

                // A new session in a non-empty folder is refused.
                var cluttered = Path.Combine(_root, "Downloads");
                Directory.CreateDirectory(cluttered);
                File.WriteAllText(Path.Combine(cluttered, "other.txt"), "x");
                ui.FolderPath = cluttered;
                await Picker(main).NewSessionCommand.ExecuteAsync(null);
                Assert.Contains("not empty", Picker(main).Error, StringComparison.Ordinal);
                Assert.False(Session.IsSession(cluttered));

                // Opening a folder that is no session is refused.
                await Picker(main).OpenExistingSessionCommand.ExecuteAsync(null);
                Assert.Contains(
                    "not a Hajde Ano session",
                    Picker(main).Error,
                    StringComparison.Ordinal
                );

                // New session in an empty folder.
                var first = Path.Combine(_root, "Mietrecht");
                ui.FolderPath = first;
                await Picker(main).NewSessionCommand.ExecuteAsync(null);
                var workspace = Assert.IsType<WorkspaceViewModel>(main.Current);
                Assert.Equal("Mietrecht", workspace.SessionName);
                Assert.Equal("Mietrecht – Hajde Ano", main.Title);
                workspace.NewCaseName = "Fall 1";
                workspace.CreateCaseCommand.Execute(null);
                workspace.CloseSessionCommand.Execute(null);

                // A second session; then the first one is opened as an existing session.
                var second = Path.Combine(_root, "Versicherung");
                ui.FolderPath = second;
                await Picker(main).NewSessionCommand.ExecuteAsync(null);
                Assert.Empty(Assert.IsType<WorkspaceViewModel>(main.Current).Cases);
                main.CloseSession();
                Assert.Equal(
                    new[] { "Versicherung", "Mietrecht" },
                    Picker(main).Recent.Select(r => r.Name)
                );

                ui.FolderPath = first;
                await Picker(main).OpenExistingSessionCommand.ExecuteAsync(null);
                workspace = Assert.IsType<WorkspaceViewModel>(main.Current);
                Assert.Equal("Fall 1", Assert.Single(workspace.Cases).Name);
                main.CloseSession();
                Assert.Equal(
                    new[] { "Mietrecht", "Versicherung" },
                    Picker(main).Recent.Select(r => r.Name)
                );

                // A moved folder is shown as unavailable and can be removed from the list.
                Directory.Move(second, second + "-moved");
                window.Close();
                services.Dispose();

                services = new AppServices(DataDir);
                window = new MainWindow();
                main = new MainWindowViewModel(services, ui);
                window.DataContext = main;
                window.Show();
                var missing = Picker(main).Recent.Single(r => r.Name == "Versicherung");
                Assert.False(missing.IsAvailable);
                Assert.Contains("not found", missing.Details, StringComparison.Ordinal);
                missing.RemoveCommand.Execute(null);
                Assert.Equal("Mietrecht", Assert.Single(Picker(main).Recent).Name);
                Assert.Single(AppSettings.Load(DataDir).RecentSessions);

                window.Close();
                services.Dispose();
                return true;
            },
            CancellationToken.None
        );
    }
}
