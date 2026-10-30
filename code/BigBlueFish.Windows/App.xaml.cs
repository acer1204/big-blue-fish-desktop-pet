// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace BigBlueFish.Windows;

public partial class App : Application
{
    public static string[] Args { get; set; } = [];

    public App()
    {
        Environment.CurrentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
#if !DEBUG
        DispatcherUnhandledException += (_, e) => { e.Handled = true; RecordException(e.Exception, false); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordException((Exception)e.ExceptionObject, true);
#endif
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // In-process verifications set --test-mode before WPF raises Startup with the test program's own
        // arguments; keep it, or test files such as pasted screenshots would land in the user's real folders.
        Args = Args.Contains("--test-mode") && !e.Args.Contains("--test-mode") ? [.. e.Args, "--test-mode"] : e.Args;
        base.OnStartup(e);
    }

    private void RecordException(Exception exception, bool fatal)
    {
        try { File.AppendAllText("companion-errors.log", DateTime.Now.ToString("s") + " " + exception + Environment.NewLine); }
        catch { }
        if (fatal) Shutdown(1);
    }
}
