using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegionFanTray;

internal static class AppPaths
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegionFanTray");

    public static string SettingsFile => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// Dirty flag. Written the moment Custom Mode is engaged, deleted on clean
    /// release. Its presence at startup means a previous run died without
    /// releasing the fans, which the handlers cannot cover (Task Manager "End
    /// task", hard power loss).
    /// </summary>
    public static string LockFile => Path.Combine(Dir, "custom-mode.lock");

    public static string CrashLog => Path.Combine(Dir, "crash.log");

    public static void EnsureDir()
    {
        try { Directory.CreateDirectory(Dir); } catch { /* best effort */ }
    }
}

internal enum ViewMode
{
    Gauge,
    Numbers,
}

internal sealed class Settings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ViewMode ViewMode { get; set; } = ViewMode.Gauge;

    public bool AutoMaxEnabled { get; set; }

    /// <summary>Enter boost at or above this CPU temperature.</summary>
    public int HighThresholdC { get; set; } = 80;

    /// <summary>
    /// Exit boost at or below this. The gap to HighThresholdC is the
    /// hysteresis: too small and the fans oscillate at the boundary.
    /// </summary>
    public int LowThresholdC { get; set; } = 70;

    public int EnterSamples { get; set; } = 3;

    public int ExitSamples { get; set; } = 5;

    public int PollIntervalMs { get; set; } = 2000;

    /// <summary>
    /// Off by default. Vantage was proven NOT to be the blocker on this
    /// machine; kept only as a diagnostic lever.
    /// </summary>
    public bool SuspendVantage { get; set; }

    public bool AutoStart { get; set; }

    /// <summary>Register the logon task with /RL HIGHEST, so no UAC prompt.</summary>
    public bool AutoStartSilent { get; set; }

    public bool ShowTempInTrayIcon { get; set; } = true;

    /// <summary>Seconds to wait for the EC to react before judging a write.</summary>
    public int SettleSeconds { get; set; } = 6;

    [JsonIgnore]
    public string? LoadWarning { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<Settings>(json, Options);
                if (s != null)
                {
                    s.Clamp();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            // Malformed settings must never stop the app from starting.
            return new Settings { LoadWarning = "settings.json was unreadable (" + ex.Message + "). Defaults restored." };
        }
        return new Settings();
    }

    /// <summary>
    /// Keeps a hand-edited file from producing a curve that oscillates or
    /// never releases. Sane bounds, not opinions.
    ///
    /// Thresholds are snapped to the same 10-degree grid the dashboard
    /// dropdowns offer, so a value read from disk always has a matching entry.
    /// </summary>
    public void Clamp()
    {
        HighThresholdC = SnapToTen(HighThresholdC, 50, 100);
        LowThresholdC = SnapToTen(LowThresholdC, 40, 90);

        // One full step of hysteresis. Too small a gap and the fans oscillate
        // at the boundary, which is worse than either state.
        if (LowThresholdC > HighThresholdC - 10) LowThresholdC = HighThresholdC - 10;
        EnterSamples = Math.Clamp(EnterSamples, 1, 60);
        ExitSamples = Math.Clamp(ExitSamples, 1, 60);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 500, 30000);
        SettleSeconds = Math.Clamp(SettleSeconds, 2, 30);
    }

    /// <summary>
    /// Floor to the nearest 10, then clamp. Flooring rather than rounding is
    /// deliberate: a lower engage point boosts sooner and a lower release point
    /// holds the fans longer, so both directions err toward more cooling.
    /// </summary>
    private static int SnapToTen(int value, int min, int max)
    {
        int snapped = (int)Math.Floor(value / 10.0) * 10;
        return Math.Clamp(snapped, min, max);
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDir();
            Clamp();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch { /* a failed save must not take the app down */ }
    }
}
