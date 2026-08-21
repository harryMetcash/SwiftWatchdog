using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftWatchdogTray;

/// <summary>
/// Mirrors SwiftWatchdog.WatchdogSettings' JSON shape. Duplicated here rather
/// than shared via a project reference — the tray and service are separately
/// published self-contained exes with no shared project today (see PipeName,
/// PauseFile constants duplicated in TrayApplicationContext for precedent).
/// </summary>
internal sealed class WatchdogSettingsDto
{
    public bool   CpuMonitoringEnabled     { get; set; } = true;
    public double CpuThresholdPercent      { get; set; } = 65.0;
    public int    CpuSustainedPeriodSeconds { get; set; } = 120;
    public string DailyRestartTime         { get; set; } = "03:00";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WatchdogSettingsDto? FromJson(string json) =>
        JsonSerializer.Deserialize<WatchdogSettingsDto>(json, JsonOptions);
}
