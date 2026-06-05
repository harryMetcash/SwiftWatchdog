using System.Threading;
using System.Windows.Forms;

namespace SwiftWatchdogTray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Surface any unhandled exceptions as a message box rather than silent crash
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "SwiftWatchdog Tray - Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject?.ToString(), "SwiftWatchdog Tray - Fatal Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        // Prevent multiple tray instances
        using var mutex = new Mutex(true, "SwiftWatchdogTray_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
