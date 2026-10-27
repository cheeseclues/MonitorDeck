using System;
using System.Threading;
using System.Windows;

namespace MonitorDeck;

public partial class App : System.Windows.Application
{
    private Mutex? _sessionMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The elevated copy performs installation only. It must not create a
        // window or a system-tray icon in the administrator's desktop session.
        if (Array.Exists(e.Args, arg => string.Equals(arg, "--install-all-users", StringComparison.OrdinalIgnoreCase)))
        {
            bool succeeded = StartupRegistration.InstallMachineWide(out _);
            Shutdown(succeeded ? 0 : 1);
            return;
        }

        // HKLM Run launches once per signing-in Windows user. Avoid a duplicate
        // tray icon if that user also launches MonitorDeck manually.
        _sessionMutex = new Mutex(false, @"Local\MonitorDeck.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            _sessionMutex.Dispose();
            _sessionMutex = null;
            Shutdown(0);
            return;
        }

        // No startup confirmation on app launch. An all-users startup install is
        // an explicit tray-menu action requiring Windows administrator approval.
        // Previously installed HKLM startup entries remain untouched.

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sessionMutex?.Dispose();
        base.OnExit(e);
    }
}
