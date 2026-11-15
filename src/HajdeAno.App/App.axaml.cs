using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HajdeAno.App.Services;
using HajdeAno.App.ViewModels;
using HajdeAno.App.Views;
using HajdeAno.Core.Storage;

namespace HajdeAno.App;

public sealed class App : Application
{
    /// <summary>Services to use instead of the defaults (screenshots/tests).</summary>
    public static AppServices? ServicesOverride { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = ServicesOverride ?? new AppServices(AppPaths.DataDirectory());
            var window = new MainWindow();
            var main = new MainWindowViewModel(services, new UiServices(window));
            window.DataContext = main;
            desktop.MainWindow = window;

            // An error in an event handler must neither crash the program nor print details;
            // the user sees the kind of error only. Drafts are saved right away.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                main.ReportInternalError(e.Exception.GetType().Name);
            };
            desktop.Exit += (_, _) =>
            {
                main.FlushDrafts();
                services.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
