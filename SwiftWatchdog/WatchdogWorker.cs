using System.IO.Pipes;
using System.ServiceProcess;

namespace SwiftWatchdog;

/// <summary>
/// Watches the SwiftPOS Service Monitoring Service and:
///   1. Restarts it immediately if found not running (status check every 60s)
///   2. Force-restarts it once per day at a configured time, regardless of status
///   3. Listens on a named pipe for PAUSE/RESUME commands from the tray app
///   4. Skips all restarts while C:\ProgramData\SwiftWatchdog\PAUSED exists
/// </summary>
public sealed class WatchdogWorker : BackgroundService
{
    // ── Configuration ─────────────────────────────────────────────────────────
    private const string TargetDisplayName    = "SwiftPOS Service Monitoring Service";
    private const int    CheckIntervalSeconds = 60;
    private static readonly TimeOnly DailyRestartTime = new(3, 0);
    private const int    TimeoutSeconds       = 120;
    private const int    StopTimeoutSeconds   = 60;
    private const int    PollSeconds          = 2;
    public  const string DataDir             = @"C:\ProgramData\SwiftWatchdog";
    private const string PauseFilePath       = DataDir + @"\PAUSED";
    private const string PipeName            = "SwiftWatchdogCtrl";
    private const int    LogRetentionDays    = 30;
    // ─────────────────────────────────────────────────────────────────────────

    private DateOnly _lastForcedRestartDate = DateOnly.MinValue;
    private DateOnly _lastPurgeDate         = DateOnly.MinValue;

    public WatchdogWorker(ILogger<WatchdogWorker> logger) { }  // logger unused; logging goes via static Log()

    private static string LogFilePath => Path.Combine(DataDir, $"watchdog-{DateTime.Now:yyyy-MM-dd}.log");
    private static bool   IsPaused    => File.Exists(PauseFilePath);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureDataDir();
        Log("Watchdog started.");
        Log($"  Status check  : every {CheckIntervalSeconds}s — restarts if not Running.");
        Log($"  Forced restart: daily at {DailyRestartTime:HH:mm} regardless of status.");
        Log($"  Log directory : {DataDir}  (retained {LogRetentionDays} days)");

        // Named pipe server runs on a background thread
        _ = Task.Run(() => RunPipeServer(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime now = DateTime.Now;

                PurgeOldLogsIfDue(now);

                if (IsPaused)
                {
                    // Silently skip — already logged at pause time
                }
                else if (IsDailyRestartDue(now))
                {
                    Log("---- Scheduled daily restart ----");
                    ForceRestart();
                    _lastForcedRestartDate = DateOnly.FromDateTime(now);
                }
                else
                {
                    CheckAndRestartIfDown();
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR in watchdog loop: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken)
                      .ContinueWith(_ => { }, CancellationToken.None);
        }

        Log("Watchdog stopped.");
    }

    // ── Named pipe server ─────────────────────────────────────────────────────

    private void RunPipeServer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Allow any local user to connect (tray app runs unprivileged)
                var security = new System.IO.Pipes.PipeSecurity();
                security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                    "Everyone",
                    System.IO.Pipes.PipeAccessRights.ReadWrite | System.IO.Pipes.PipeAccessRights.CreateNewInstance,
                    System.Security.AccessControl.AccessControlType.Allow));

                var pipe = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None,
                    inBufferSize: 256, outBufferSize: 256,
                    security);

                // Hoist command outside try so it's accessible after the pipe is disposed
                string? command = null;
                try
                {
                    pipe.WaitForConnection();

                    // Read command using raw bytes — StreamReader.leaveOpen is unreliable on PipeStream
                    // and disposes the underlying pipe prematurely
                    var buffer = new byte[64];
                    int bytesRead = pipe.Read(buffer, 0, buffer.Length);
                    command = System.Text.Encoding.UTF8
                        .GetString(buffer, 0, bytesRead)
                        .Trim()
                        .TrimEnd('\r', '\n')
                        .ToUpperInvariant();

                    Log($"Pipe command received: {command}");

                    // Write response as raw bytes for the same reason
                    string response = (command is "PAUSE" or "RESUME") ? "OK\n" : "ERR: Unknown command\n";
                    byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(response);
                    pipe.Write(responseBytes, 0, responseBytes.Length);
                    pipe.Flush();
                    pipe.WaitForPipeDrain();
                    pipe.Disconnect();

                    if (response.StartsWith("ERR")) command = null; // don't run handler
                }
                finally { pipe.Dispose(); }

                // Now do the work — pipe is already closed so no timing issues
                if (command == "PAUSE")       HandlePause();
                else if (command == "RESUME") HandleResume();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"WARNING: Pipe server error: {ex.Message}");
                Thread.Sleep(1000); // brief back-off before re-listening
            }
        }
    }

    private void HandlePause()
    {
        try
        {
            // Write PAUSED flag
            File.WriteAllText(PauseFilePath, DateTime.Now.ToString("o"));
            Log("Watchdog PAUSED by tray app.");

            // Stop the SwiftPOS service
            ServiceController? svc = ResolveService(silent: true);
            if (svc is not null)
            {
                svc.Refresh();
                if (svc.Status == ServiceControllerStatus.Running)
                {
                    Log("Stopping SwiftPOS service (pause requested)...");
                    RunSc($"stop \"{svc.ServiceName}\"");

                    DateTime deadline = DateTime.UtcNow.AddSeconds(StopTimeoutSeconds);
                    do { Thread.Sleep(1000); svc.Refresh(); }
                    while (svc.Status != ServiceControllerStatus.Stopped && DateTime.UtcNow < deadline);

                    svc.Refresh();
                    Log($"SwiftPOS service status after pause: {svc.Status}");
                }
            }

        }
        catch (Exception ex)
        {
            Log($"ERROR in HandlePause: {ex.Message}");
        }
    }

    private void HandleResume()
    {
        try
        {
            // Remove PAUSED flag
            if (File.Exists(PauseFilePath))
                File.Delete(PauseFilePath);

            Log("Watchdog RESUMED by tray app.");

            // Start the SwiftPOS service
            ServiceController? svc = ResolveService(silent: true);
            if (svc is not null)
            {
                svc.Refresh();
                if (svc.Status != ServiceControllerStatus.Running)
                {
                    Log("Starting SwiftPOS service (resume requested)...");
                    StartService(svc);
                }
            }

        }
        catch (Exception ex)
        {
            Log($"ERROR in HandleResume: {ex.Message}");
        }
    }

    // ── Watchdog logic ────────────────────────────────────────────────────────

    private bool IsDailyRestartDue(DateTime now)
    {
        DateOnly today   = DateOnly.FromDateTime(now);
        TimeOnly timeNow = TimeOnly.FromDateTime(now);
        return timeNow >= DailyRestartTime && _lastForcedRestartDate < today;
    }

    private void PurgeOldLogsIfDue(DateTime now)
    {
        DateOnly today = DateOnly.FromDateTime(now);
        if (_lastPurgeDate >= today) return;
        _lastPurgeDate = today;

        try
        {
            var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
            foreach (var file in Directory.GetFiles(DataDir, "watchdog-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    Log($"Purged old log: {Path.GetFileName(file)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WARNING: Log purge failed: {ex.Message}");
        }
    }

    private void CheckAndRestartIfDown()
    {
        ServiceController? svc = ResolveService();
        if (svc is null) return;

        svc.Refresh();
        if (svc.Status == ServiceControllerStatus.Running) return;

        Log($"Service is {svc.Status} — unexpected. Initiating recovery restart.");
        RestartService(svc);
    }

    private void ForceRestart()
    {
        ServiceController? svc = ResolveService();
        if (svc is null) return;

        svc.Refresh();
        Log($"Force-stopping service (current status: {svc.Status})...");

        RunSc($"stop \"{svc.ServiceName}\"");

        DateTime stopDeadline = DateTime.UtcNow.AddSeconds(StopTimeoutSeconds);
        do { Thread.Sleep(1000); svc.Refresh(); }
        while (svc.Status != ServiceControllerStatus.Stopped && DateTime.UtcNow < stopDeadline);

        svc.Refresh();
        if (svc.Status != ServiceControllerStatus.Stopped)
        {
            Log($"WARNING: Service did not stop within {StopTimeoutSeconds}s (Status={svc.Status}). Killing process...");
            KillServiceProcess(svc.ServiceName);
            Thread.Sleep(2000);
        }
        else
        {
            Log("Service stopped.");
        }

        StartService(svc);
    }

    private void RestartService(ServiceController svc)
    {
        svc.Refresh();

        if (svc.Status != ServiceControllerStatus.Stopped &&
            svc.Status != ServiceControllerStatus.StopPending)
        {
            try
            {
                Log("Stopping service...");
                svc.Stop();
                DateTime stopDeadline = DateTime.UtcNow.AddSeconds(StopTimeoutSeconds);
                while (svc.Status != ServiceControllerStatus.Stopped && DateTime.UtcNow < stopDeadline)
                {
                    Thread.Sleep(1000);
                    svc.Refresh();
                }
            }
            catch (Exception ex) { Log($"WARNING: Error stopping service: {ex.Message}"); }
        }

        svc.Refresh();
        if (svc.Status != ServiceControllerStatus.Stopped)
            Log($"WARNING: Service did not reach Stopped (current: {svc.Status}). Attempting start anyway.");
        else
            Log("Service stopped.");

        StartService(svc);
    }

    private void StartService(ServiceController svc)
    {
        try
        {
            Log("Starting service...");
            svc.Start();

            DateTime deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
            while (svc.Status != ServiceControllerStatus.Running && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(PollSeconds * 1000);
                svc.Refresh();
                Log($"Waiting... Status={svc.Status}");
            }

            svc.Refresh();
            if (svc.Status == ServiceControllerStatus.Running)
                Log("SUCCESS: Service is Running.");
            else
                Log($"FAIL: Service did not reach Running within {TimeoutSeconds}s. Final status: {svc.Status}");
        }
        catch (Exception ex) { Log($"ERROR starting service: {ex.Message}"); }
    }

    private ServiceController? ResolveService(bool silent = false)
    {
        ServiceController? svc = ServiceController
            .GetServices()
            .FirstOrDefault(s => s.DisplayName.Equals(TargetDisplayName, StringComparison.OrdinalIgnoreCase));

        if (svc is null && !silent)
            Log($"WARNING: Service '{TargetDisplayName}' not found on this machine.");

        return svc;
    }

    private static void KillServiceProcess(string serviceName)
    {
        try
        {
            var output  = RunScCapture($"queryex \"{serviceName}\"");
            var pidLine = output.Split('\n')
                .FirstOrDefault(l => l.TrimStart().StartsWith("PID", StringComparison.OrdinalIgnoreCase));

            if (pidLine is not null)
            {
                var pidStr = pidLine.Split(':').LastOrDefault()?.Trim();
                if (int.TryParse(pidStr, out int pid) && pid > 0)
                {
                    System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
                    Log($"Killed process PID={pid}.");
                    return;
                }
            }
            Log("WARNING: Could not determine service PID to kill.");
        }
        catch (Exception ex) { Log($"WARNING: KillServiceProcess failed: {ex.Message}"); }
    }

    private static void RunSc(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
        }
        catch (Exception ex) { Log($"WARNING: sc.exe call failed: {ex.Message}"); }
    }

    private static string RunScCapture(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        catch { return string.Empty; }
    }

    private static void EnsureDataDir()
    {
        try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); }
        catch { /* best effort */ }
    }

    private static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        Console.WriteLine(line);
        try { EnsureDataDir(); File.AppendAllText(LogFilePath, line + Environment.NewLine); }
        catch { /* don't crash the watchdog */ }
    }
}
