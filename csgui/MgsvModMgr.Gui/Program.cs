using System;
using System.IO;
using System.Linq;
using Avalonia;

namespace MgsvModMgr.Gui;

internal static class Program
{
    /// <summary>
    /// Path to a single-line file the second instance writes to when it
    /// gets handed an nxm:// URL but another copy of the app is already
    /// running. The running instance watches this file and processes
    /// the URL on its own UI thread.
    /// </summary>
    public static readonly string NxmInboxPath = Path.Combine(
        AppContext.BaseDirectory, "nxm_inbox.txt");

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

        // nxm:// URL routing. If we were launched via the registered
        // nxm protocol handler the URL comes through as args[0].
        var nxmUrl = args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));

        // Single-instance via a system-wide named mutex. If another
        // copy of the app is already up, we just queue the URL (if any)
        // for the running instance to pick up and exit immediately.
        var mutex = new System.Threading.Mutex(initiallyOwned: true,
                                               name: "Global\\mgsv_modmgr_singleton",
                                               createdNew: out var first);

        if (!first)
        {
            if (!string.IsNullOrEmpty(nxmUrl))
            {
                try { File.WriteAllText(NxmInboxPath, nxmUrl); } catch { }
            }
            return 0;
        }

        // First instance — make the URL (if any) available to the VM
        // through a known env var. MainViewModel checks for it after
        // the window has opened.
        if (!string.IsNullOrEmpty(nxmUrl))
            Environment.SetEnvironmentVariable("MGSV_PENDING_NXM", nxmUrl);

        // Best-effort register ourselves as the nxm:// handler on Windows
        // so the user's browser routes "Mod Manager Download" clicks
        // back to us. No-op on non-Windows for now.
        TryRegisterNxmHandler();

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
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }

    /// <summary>Public so the Avalonia visual designer / previewer can find it.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Register modmgr_gui.exe as the OS-wide handler for nxm:// URLs.
    /// Lives under HKCU so no admin elevation is required. Writes the
    /// minimal three keys the Windows shell needs to route a protocol
    /// click to our exe with the URL as the first argument.
    /// </summary>
    private static void TryRegisterNxmHandler()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            using var nxm = Microsoft.Win32.Registry.CurrentUser
                .CreateSubKey(@"Software\Classes\nxm", writable: true);
            nxm.SetValue("",            "URL:Nexus Mods Protocol");
            nxm.SetValue("URL Protocol", "");

            using var icon = nxm.CreateSubKey("DefaultIcon", writable: true);
            icon.SetValue("", $"\"{exe}\",0");

            using var shell   = nxm.CreateSubKey(@"shell\open\command", writable: true);
            shell.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch
        {
            // Registration is best-effort. If it fails the user can
            // still launch the app and we just won't receive nxm clicks.
        }
    }

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
