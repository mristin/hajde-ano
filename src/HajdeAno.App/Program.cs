using Avalonia;

namespace HajdeAno.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
        {
            return SelfTest.RunAsync(args[1..]).GetAwaiter().GetResult();
        }

        // Privacy: never write crash details (which might contain private text) anywhere.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine(
                $"Unexpected error: {(e.ExceptionObject as Exception)?.GetType().Name ?? "unknown"}"
            );

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer and the screenshot tool.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
