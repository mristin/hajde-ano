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

        InstallCrashHandlers();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Privacy: never print crash details (messages and stack traces may contain private text
    /// or file paths with names), and never abort (which can write a core dump or an error
    /// report with the memory of the process).
    /// </summary>
    internal static void InstallCrashHandlers()
    {
        // Exiting from the handler prevents the runtime from printing the exception and
        // from aborting the process.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(
                $"Hajde Ano stopped because of an internal error ({(e.ExceptionObject as Exception)?.GetType().Name ?? "unknown"})."
            );
            Console.Error.Flush();
            Environment.Exit(1);
        };

        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    // Also used by the visual designer and the screenshot tool.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
