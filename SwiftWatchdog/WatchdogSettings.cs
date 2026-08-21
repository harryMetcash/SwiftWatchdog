using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwiftWatchdog;

/// <summary>
/// Immutable watchdog configuration, persisted to settings.json and swapped
/// in atomically by WatchdogWorker whenever the tray sends new values.
/// </summary>
public sealed class WatchdogSettings
{
    public const double MinCpuThresholdPercent = 1.0;
    public const double MaxCpuThresholdPercent = 1000.0;
    public const int MinCpuSustainedPeriodSeconds = 10;
    public const int MaxCpuSustainedPeriodSeconds = 36000;

    private const string TimeFormat = "HH:mm";

    public bool CpuMonitoringEnabled { get; init; } = true;
    public double CpuThresholdPercent { get; init; } = 65.0;
    public int CpuSustainedPeriodSeconds { get; init; } = 120;

    /// <summary>24-hour "HH:mm" string — stored as text (not TimeOnly) so the
    /// JSON wire format is unambiguous between the two separately-compiled
    /// projects that both need to read/write it.</summary>
    public string DailyRestartTime { get; init; } = "03:00";

    public static WatchdogSettings Default => new();

    public TimeOnly GetDailyRestartTimeOnly() =>
        TimeOnly.TryParseExact(DailyRestartTime, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t)
            ? t
            : new TimeOnly(3, 0);

    /// <summary>Returns a new instance with every field clamped to its valid range.</summary>
    public WatchdogSettings Clamp() => new()
    {
        CpuMonitoringEnabled = CpuMonitoringEnabled,
        CpuThresholdPercent = Math.Clamp(CpuThresholdPercent, MinCpuThresholdPercent, MaxCpuThresholdPercent),
        CpuSustainedPeriodSeconds = Math.Clamp(CpuSustainedPeriodSeconds, MinCpuSustainedPeriodSeconds, MaxCpuSustainedPeriodSeconds),
        DailyRestartTime = NormalizeTimeString(DailyRestartTime),
    };

    private static string NormalizeTimeString(string value) =>
        TimeOnly.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t)
            ? t.ToString(TimeFormat, CultureInfo.InvariantCulture)
            : "03:00";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static bool TryParse(string json, out WatchdogSettings settings, out string error)
    {
        try
        {
            WatchdogSettings? parsed = JsonSerializer.Deserialize<WatchdogSettings>(json, JsonOptions);
            if (parsed is null)
            {
                settings = Default;
                error = "Empty settings payload.";
                return false;
            }

            settings = parsed.Clamp();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            settings = Default;
            error = $"Invalid settings JSON: {ex.Message}";
            return false;
        }
    }

    public static WatchdogSettings LoadOrDefault(string path, Action<string> log)
    {
        try
        {
            if (!File.Exists(path)) return Default;

            string json = File.ReadAllText(path);
            if (TryParse(json, out WatchdogSettings settings, out string error)) return settings;

            log($"WARNING: settings.json invalid ({error}); using defaults.");
            return Default;
        }
        catch (Exception ex)
        {
            log($"WARNING: Failed to load settings.json ({ex.Message}); using defaults.");
            return Default;
        }
    }

    /// <summary>Writes via a temp file + rename so a crash mid-write can't corrupt settings.json.</summary>
    public void SaveAtomic(string path)
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, ToJson());
        File.Move(tempPath, path, overwrite: true);
    }
}
