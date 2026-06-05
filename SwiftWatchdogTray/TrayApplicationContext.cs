using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows.Forms;

namespace SwiftWatchdogTray;

/// <summary>
/// Drives the system tray icon and right-click context menu.
/// Communicates with SwiftWatchdog service via named pipe — no elevation needed.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string PipeName    = "SwiftWatchdogCtrl";
    private const string PauseFile   = @"C:\ProgramData\SwiftWatchdog\PAUSED";
    private const int    PipeTimeout = 8000;

    private readonly NotifyIcon        _trayIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resumeItem;

    private readonly Icon _iconActive;
    private readonly Icon _iconPaused;

    public TrayApplicationContext()
    {
        _iconActive = LoadEmbeddedIcon("icon_active.ico");
        _iconPaused = LoadEmbeddedIcon("icon_paused.ico");

        _pauseItem  = new ToolStripMenuItem("Pause Watchdog",  null, OnPause);
        _resumeItem = new ToolStripMenuItem("Resume Watchdog", null, OnResume);
        var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        menu.Opening += (_, _) => RefreshMenuState();

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible          = true,
        };

        RefreshMenuState();
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private bool IsPaused => File.Exists(PauseFile);

    private void RefreshMenuState()
    {
        bool paused = IsPaused;

        // Force Windows to repaint the tray icon — it caches aggressively
        // and won't update reliably if we just set the same icon object again
        _trayIcon.Visible = false;
        _trayIcon.Icon    = paused ? _iconPaused : _iconActive;
        _trayIcon.Text    = paused
            ? "SwiftWatchdog - Paused"
            : "SwiftWatchdog - Active";
        _trayIcon.Visible = true;

        _pauseItem.Enabled  = !paused;
        _resumeItem.Enabled = paused;
    }

    // ── Menu handlers ─────────────────────────────────────────────────────────

    private void OnPause(object? sender, EventArgs e)
    {
        string result = SendCommand("PAUSE");
        if (result.StartsWith("ERR"))
        {
            MessageBox.Show(result, "SwiftWatchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Brief delay so the service has time to write the PAUSED file before we check it
        System.Threading.Thread.Sleep(300);
        RefreshMenuState();
    }

    private void OnResume(object? sender, EventArgs e)
    {
        string result = SendCommand("RESUME");
        if (result.StartsWith("ERR"))
        {
            MessageBox.Show(result, "SwiftWatchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Brief delay so the service has time to delete the PAUSED file before we check it
        System.Threading.Thread.Sleep(300);
        RefreshMenuState();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // ── Named pipe comms ──────────────────────────────────────────────────────

    private static string SendCommand(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(PipeTimeout);

            // Use raw bytes — StreamReader/Writer on PipeStream can close the pipe prematurely
            byte[] commandBytes = System.Text.Encoding.UTF8.GetBytes(command + "\n");
            pipe.Write(commandBytes, 0, commandBytes.Length);
            pipe.Flush();

            var buffer = new byte[64];
            int bytesRead = pipe.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8
                .GetString(buffer, 0, bytesRead)
                .Trim();
        }
        catch (TimeoutException)
        {
            return "ERR: Could not connect to SwiftWatchdog service. Is it running?";
        }
        catch (Exception ex)
        {
            return $"ERR: {ex.Message}";
        }
    }

    // ── Icon loader ───────────────────────────────────────────────────────────

    private static Icon LoadEmbeddedIcon(string filename)
    {
        var asm = Assembly.GetExecutingAssembly();

        // For single-file publish, embedded resource names include subfolder:
        // Try "SwiftWatchdogTray.Resources.<filename>" first, then flat name as fallback
        string[] candidates =
        [
            $"SwiftWatchdogTray.Resources.{filename}",
            $"SwiftWatchdogTray.{filename}",
        ];

        foreach (var name in candidates)
        {
            var stream = asm.GetManifestResourceStream(name);
            if (stream is not null)
                return new Icon(stream);
        }

        // Diagnostic: list what's actually embedded so we can fix the name if wrong
        var available = string.Join("\n", asm.GetManifestResourceNames());
        throw new InvalidOperationException(
            $"Could not find embedded icon '{filename}'.\nAvailable resources:\n{available}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _iconActive.Dispose();
            _iconPaused.Dispose();
        }
        base.Dispose(disposing);
    }
}
