using SwiftWatchdog;

// ── Self-install / uninstall handling ────────────────────────────────────────
// Run with --install  → registers as a Windows service (requires elevation)
// Run with --uninstall → removes the service
// Run normally        → starts the watchdog (used by SCM and direct invocation)

string exePath = Environment.ProcessPath
    ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "--install":
            ServiceInstaller.Install(exePath);
            return;

        case "--uninstall":
            ServiceInstaller.Uninstall();
            return;
    }
}

// ── Normal execution (service host) ──────────────────────────────────────────
IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = ServiceInstaller.ServiceName;
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<WatchdogWorker>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .Build();

host.Run();
