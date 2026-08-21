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
    private const int    TimeoutSeconds       = 120;
    private const int    StopTimeoutSeconds   = 60;
    private const int    PollSeconds          = 2;
    public  const string DataDir             = @"C:\ProgramData\SwiftWatchdog";
    private const string PauseFilePath       = DataDir + @"\PAUSED";
    private const string PipeName            = "SwiftWatchdogCtrl";
    private const int    PipeBufferSize      = 8192;
    private const int    LogRetentionDays    = 30;
    private const int    CpuSampleIntervalSeconds  = 10;
    private const int    CpuRestartCooldownMinutes = 5;
    // ─────────────────────────────────────────────────────────────────────────

    private DateOnly _lastForcedRestartDate = DateOnly.MinValue;
    private DateOnly _lastPurgeDate         = DateOnly.MinValue;

    private WatchdogSettings _settings = WatchdogSettings.Default;
    private readonly object  _restartLock = new();

    private DateTime? _highCpuSince;
    private double    _highCpuMin;
    private double    _highCpuMax;
    private DateTime  _cpuCooldownUntil = DateTime.MinValue;
    private System.Diagnostics.Process? _monitoredProcess;
    private double    _lastCpuTotalSeconds;
    private DateTime  _lastCpuSampleTime;

    public WatchdogWorker(ILogger<WatchdogWorker> logger) { }  // logger unused; logging goes via static Log()

    private static string LogFilePath => Path.Combine(DataDir, $"watchdog-{DateTime.Now:yyyy-MM-dd}.log");
    private static string SettingsFilePath => Path.Combine(DataDir, "settings.json");
    private static bool   IsPaused    => File.Exists(PauseFilePath);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureDataDir();
        _settings = WatchdogSettings.LoadOrDefault(SettingsFilePath, Log);

        Log("Watchdog started.");
        Log($"  Status check  : every {CheckIntervalSeconds}s — restarts if not Running.");
        Log($"  Forced restart: daily at {_settings.DailyRestartTime} regardless of status.");
        Log($"  CPU monitor   : {(_settings.CpuMonitoringEnabled ? "enabled" : "disabled")} — " +
            $"threshold {_settings.CpuThresholdPercent}% (per-core) sustained {_settings.CpuSustainedPeriodSeconds}s.");
        Log($"  Log directory : {DataDir}  (retained {LogRetentionDays} days)");

        // Named pipe server and CPU monitor each run on their own background thread
        _ = Task.Run(() => RunPipeServer(stoppingToken), stoppingToken);
        _ = Task.Run(() => RunCpuMonitorLoop(stoppingToken), stoppingToken);

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
                    lock (_restartLock) { ForceRestart(); }
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
                    inBufferSize: PipeBufferSize, outBufferSize: PipeBufferSize,
                    security);

                // Hoist outside the inner try so it's accessible after the pipe is disposed —
                // PAUSE/RESUME are fire-and-forget: respond first (recognized keyword only),
                // then actually pause/resume after the pipe closes. GETSETTINGS/SETSETTINGS are
                // NOT fire-and-forget — they validate and respond with the real outcome while the
                // pipe is still open, so the tray gets an accurate result.
                string? deferredCommand = null;
                try
                {
                    pipe.WaitForConnection();

                    string raw = ReadPipeMessage(pipe, PipeBufferSize);
                    (string command, string payload) = ParsePipeMessage(raw);
                    Log($"Pipe command received: {command}");

                    switch (command)
                    {
                        case "PAUSE":
                        case "RESUME":
                            WritePipeResponse(pipe, "OK");
                            deferredCommand = command;
                            break;

                        case "GETSETTINGS":
                            WritePipeResponse(pipe, $"OK {_settings.ToJson()}");
                            break;

                        case "SETSETTINGS":
                            HandleSetSettings(pipe, payload);
                            break;

                        default:
                            WritePipeResponse(pipe, "ERR: Unknown command");
                            break;
                    }

                    pipe.WaitForPipeDrain();
                    pipe.Disconnect();
                }
                finally { pipe.Dispose(); }

                // Now do the work — pipe is already closed so no timing issues
                if (deferredCommand == "PAUSE")       HandlePause();
                else if (deferredCommand == "RESUME") HandleResume();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"WARNING: Pipe server error: {ex.Message}");
                Thread.Sleep(1000); // brief back-off before re-listening
            }
        }
    }

    private static string ReadPipeMessage(System.IO.Pipes.PipeStream pipe, int maxBytes)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[1024];
        while (ms.Length < maxBytes)
        {
            int bytesRead = pipe.Read(chunk, 0, chunk.Length);
            if (bytesRead <= 0) break;
            ms.Write(chunk, 0, bytesRead);
            if (Array.IndexOf(chunk, (byte)'\n', 0, bytesRead) >= 0) break;
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static (string Command, string Payload) ParsePipeMessage(string raw)
    {
        string trimmed = raw.TrimEnd('\r', '\n');
        int spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx < 0
            ? (trimmed.ToUpperInvariant(), string.Empty)
            : (trimmed[..spaceIdx].ToUpperInvariant(), trimmed[(spaceIdx + 1)..]);
    }

    private static void WritePipeResponse(System.IO.Pipes.PipeStream pipe, string message)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message + "\n");
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
    }

    private void HandleSetSettings(System.IO.Pipes.PipeStream pipe, string payload)
    {
        if (!WatchdogSettings.TryParse(payload, out WatchdogSettings parsed, out string parseError))
        {
            WritePipeResponse(pipe, $"ERR: {parseError}");
            return;
        }

        try
        {
            parsed.SaveAtomic(SettingsFilePath);
            _settings = parsed; // atomic reference swap — readers snapshot _settings at loop-top
            Log("Settings updated via tray: " +
                $"CPU monitoring={parsed.CpuMonitoringEnabled}, threshold={parsed.CpuThresholdPercent}%, " +
                $"sustained={parsed.CpuSustainedPeriodSeconds}s, dailyRestart={parsed.DailyRestartTime}.");
            WritePipeResponse(pipe, "OK");
        }
        catch (Exception ex)
        {
            Log($"WARNING: Failed to save settings.json: {ex.Message}");
            WritePipeResponse(pipe, $"ERR: Failed to save settings: {ex.Message}");
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
        TimeOnly restartTime = _settings.GetDailyRestartTimeOnly();
        return timeNow >= restartTime && _lastForcedRestartDate < today;
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
        lock (_restartLock) { RestartService(svc); }
    }

    // ── CPU monitoring ────────────────────────────────────────────────────────

    private async Task RunCpuMonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SampleCpuOnce();
            }
            catch (Exception ex)
            {
                Log($"WARNING: CPU monitor error: {ex.Message}");
                ResetCpuTracking();
            }

            await Task.Delay(TimeSpan.FromSeconds(CpuSampleIntervalSeconds), ct)
                      .ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private void SampleCpuOnce()
    {
        WatchdogSettings settings = _settings; // snapshot — avoids torn reads if SETSETTINGS swaps mid-tick

        if (IsPaused || !settings.CpuMonitoringEnabled)
        {
            ResetCpuTracking();
            return;
        }

        if (DateTime.UtcNow < _cpuCooldownUntil)
            return;

        bool needsBaseline = _monitoredProcess is null;
        if (!needsBaseline)
        {
            try { needsBaseline = _monitoredProcess!.HasExited; }
            catch { needsBaseline = true; }
        }

        if (needsBaseline)
        {
            ResetCpuTracking();
            TryResolveMonitoredProcess(); // primes the baseline for next tick; no-op if service isn't found
            return;
        }

        double cpuNowSeconds;
        DateTime now = DateTime.UtcNow;
        try
        {
            cpuNowSeconds = _monitoredProcess!.TotalProcessorTime.TotalSeconds;
        }
        catch
        {
            // Process likely exited between the HasExited check above and now
            ResetCpuTracking();
            return;
        }

        double deltaCpuMs  = (cpuNowSeconds - _lastCpuTotalSeconds) * 1000.0;
        double deltaWallMs = (now - _lastCpuSampleTime).TotalMilliseconds;
        _lastCpuTotalSeconds = cpuNowSeconds;
        _lastCpuSampleTime   = now;

        if (deltaWallMs <= 0) return;

        // Raw per-core: 100% = one full logical core saturated (not divided by core count).
        // Chosen for portability across tills with different core counts — see design doc.
        double cpuPercent = (deltaCpuMs / deltaWallMs) * 100.0;

        if (cpuPercent >= settings.CpuThresholdPercent)
        {
            if (_highCpuSince is null)
            {
                _highCpuSince = now;
                _highCpuMin = cpuPercent;
                _highCpuMax = cpuPercent;
                Log($"High CPU usage detected: {cpuPercent:F1}% (threshold {settings.CpuThresholdPercent:F1}%).");
                return;
            }

            _highCpuMin = Math.Min(_highCpuMin, cpuPercent);
            _highCpuMax = Math.Max(_highCpuMax, cpuPercent);

            double sustainedSeconds = (now - _highCpuSince.Value).TotalSeconds;
            if (sustainedSeconds >= settings.CpuSustainedPeriodSeconds)
            {
                Log($"CPU threshold exceeded for {sustainedSeconds:F0}s " +
                    $"(min={_highCpuMin:F1}%, max={_highCpuMax:F1}%). Forcing restart.");

                lock (_restartLock) { ForceRestart(); }

                _cpuCooldownUntil = DateTime.UtcNow.AddMinutes(CpuRestartCooldownMinutes);
                ResetCpuTracking();
            }
        }
        else
        {
            if (_highCpuSince is not null)
                Log($"CPU usage returned to normal: {cpuPercent:F1}%.");
            _highCpuSince = null;
        }
    }

    private bool TryResolveMonitoredProcess()
    {
        ServiceController? svc = ResolveService(silent: true);
        if (svc is null) return false;

        int? pid = GetServicePid(svc.ServiceName);
        if (pid is not int p) return false;

        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(p);
            _monitoredProcess    = proc;
            _lastCpuTotalSeconds = proc.TotalProcessorTime.TotalSeconds;
            _lastCpuSampleTime   = DateTime.UtcNow;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ResetCpuTracking()
    {
        _highCpuSince     = null;
        _monitoredProcess = null;
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

    /// <summary>Resolves a service's current process ID via sc.exe. Returns null if
    /// the service isn't found, isn't running, or the output couldn't be parsed.</summary>
    private static int? GetServicePid(string serviceName)
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
                    return pid;
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    private static void KillServiceProcess(string serviceName)
    {
        int? pid = GetServicePid(serviceName);
        if (pid is int p)
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(p).Kill(entireProcessTree: true);
                Log($"Killed process PID={p}.");
                return;
            }
            catch (Exception ex) { Log($"WARNING: Failed to kill PID={p}: {ex.Message}"); return; }
        }
        Log("WARNING: Could not determine service PID to kill.");
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
