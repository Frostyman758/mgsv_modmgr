using System;
using System.IO;
using Avalonia;

namespace MgsvModMgr.Gui;

internal static class Program
{
    /// <summary>
    /// Entry point. Wires global exception handlers to a side-by-side crash
    /// log before handing off to Avalonia. Don't reference Avalonia or
    /// SynchronizationContext-dependent APIs before <see cref="BuildAvaloniaApp"/>
    /// runs.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => WriteCrashLog(e.ExceptionObject as Exception, "UnhandledException");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException +=
            (_, e) => WriteCrashLog(e.Exception, "UnobservedTaskException");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "StartupException");
            throw;
        }
    }

    /// <summary>Public so the Avalonia visual designer / previewer can find it.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void WriteCrashLog(Exception? ex, string source)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "modmgr_crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}----{Environment.NewLine}");
        }
        catch
        {
            // Nothing more we can do; don't let the logger itself crash the process.
        }
    }
}
