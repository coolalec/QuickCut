using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace QuickCut;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = true;
        // A WindowChrome hit-test glitch (see WindowEffects.FixHitTestOverflow) is harmless; don't alarm the user with it.
        if (e.Exception is OverflowException && (e.Exception.StackTrace ?? "").Contains("WindowChromeWorker")) return;
        MessageBox.Show(e.Exception.Message, "QuickCut", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Appends the exception to QuickCut.crash.log next to the exe, for diagnosing problems on users' machines.</summary>
    static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QuickCut.crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
