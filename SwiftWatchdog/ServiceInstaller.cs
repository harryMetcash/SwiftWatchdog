using System.Diagnostics;
using Microsoft.Win32;

namespace SwiftWatchdog;

/// <summary>
/// Handles self-installation and removal of the watchdog Windows service
/// using sc.exe (ships with every Windows version).
/// Must be run elevated — the caller (UAC prompt) handles that.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName  = "SwiftWatchdog";
    public const string DisplayName  = "SwiftPOS Watchdog";
    public const string Description  = "Monitors Swiftpos Service Monitor for faults & restarts daily";

    public static void Install(string exePath)
    {
        Console.WriteLine($"Installing service '{ServiceName}'...");

        // Create service: auto-start, runs as LocalSystem
        RunSc($"create \"{ServiceName}\" binPath= \"{exePath}\" start= auto DisplayName= \"{DisplayName}\"");

        // Set description via registry — avoids sc.exe argument escaping issues
        SetDescriptionViaRegistry(Description);

        // Configure recovery: restart on failure (3 attempts, 1-min delay, reset after 1 day)
        RunSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000");

        // Start it — fire and forget, don't wait for the service host process
        RunScNoWait($"start \"{ServiceName}\"");

        Console.WriteLine($"Done. '{DisplayName}' installed.");
        // No Pause() — this exe is invoked by the Inno Setup installer silently
    }

    public static void Uninstall()
    {
        Console.WriteLine($"Removing service '{ServiceName}'...");

        RunSc($"stop \"{ServiceName}\"");
        System.Threading.Thread.Sleep(2000);
        RunSc($"delete \"{ServiceName}\"");

        Console.WriteLine($"Service '{ServiceName}' removed.");
        // No Pause()
    }

    private static void SetDescriptionViaRegistry(string description)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: true);

            if (key is null)
            {
                Console.WriteLine("WARNING: Could not open service registry key to set description.");
                return;
            }

            key.SetValue("Description", description, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Failed to set description via registry: {ex.Message}");
        }
    }

    /// <summary>Runs sc.exe and waits for it to exit (for registration commands).</summary>
    private static void RunSc(string arguments)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.Trim());

        if (proc.ExitCode != 0 && proc.ExitCode != 1062)
            Console.WriteLine($"  sc.exe exited {proc.ExitCode} for: sc {arguments}");
    }

    /// <summary>Runs sc.exe without waiting — used for 'sc start' so the installer
    /// doesn't block on the service host process staying alive.</summary>
    private static void RunScNoWait(string arguments)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        Process.Start(psi); // intentionally not waited on
    }
}
