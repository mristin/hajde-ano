using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            window.DataContext = new MainWindowViewModel(services, new UiServices(window));
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
