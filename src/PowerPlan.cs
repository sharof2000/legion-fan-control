using System.Text.RegularExpressions;

namespace LegionFanTray;

/// <summary>One powercfg setting, addressed by subgroup and setting alias.</summary>
internal sealed record PowerSetting(string Sub, string Setting)
{
    public string Path => Sub + " " + Setting;

    /// <summary>Key under which a pre-toggle value is remembered in settings.json.</summary>
    public string SavedKey(string rail) => Sub + "/" + Setting + "/" + rail;
}

/// <summary>
/// A switch row on the GPU / Power tab: one or more powercfg settings written
/// together, and restored together to whatever they held before.
///
/// A null rail is left alone entirely -- not written, not captured, not
/// restored -- so a toggle whose caption says "on battery" really only touches
/// the DC rail.
/// </summary>
internal sealed class PowerToggle
{
    public required string Caption { get; init; }
    public required string Tooltip { get; init; }
    public required PowerSetting[] Settings { get; init; }
    public int? OnAc { get; init; }
    public int? OnDc { get; init; }

    /// <summary>Formats the live per-rail values for the switch sub-caption.</summary>
    public required Func<(int? Ac, int? Dc)[], string> Describe { get; init; }
}

/// <summary>
/// powercfg wrapper. Reads and writes a named setting on the active scheme and
/// verifies by read-back, because powercfg exits 0 even when nothing changed --
/// the same trap CpuCap already documents.
///
/// Everything here is scheme-scoped: values live per power scheme, so switching
/// plans drops them. That is why the dashboard re-reads on every open rather
/// than trusting what it wrote last time.
/// </summary>
internal static class PowerPlan
{
    public const string Scheme = "SCHEME_CURRENT";

    // --- the three switch rows ---------------------------------------------

    public static readonly PowerSetting ProcThrottleMin = new("SUB_PROCESSOR", "PROCTHROTTLEMIN");
    public static readonly PowerSetting ProcThrottleMax = new("SUB_PROCESSOR", "PROCTHROTTLEMAX");
    public static readonly PowerSetting EsBattThreshold = new("SUB_ENERGYSAVER", "ESBATTTHRESHOLD");
    public static readonly PowerSetting PcieAspm = new("SUB_PCIEXPRESS", "ASPM");

    /// <summary>
    /// Windows throttles the CPU on DC even under a performance plan. Pinning
    /// min and max to 100 on both rails means unplugging no longer changes
    /// clocks mid-session. Costs battery life, by design.
    /// </summary>
    public static readonly PowerToggle LockCpuBothRails = new()
    {
        Caption = "Lock CPU to 100% on AC and battery",
        Tooltip = "Sets PROCTHROTTLEMIN and PROCTHROTTLEMAX to 100 on both rails,\r\n" +
                  "so plugging and unplugging no longer changes CPU clocks.\r\n" +
                  "Costs battery life. Turning this off restores the previous values.",
        Settings = new[] { ProcThrottleMin, ProcThrottleMax },
        OnAc = 100,
        OnDc = 100,
        Describe = v => "min " + Rail(v[0]) + "  ·  max " + Rail(v[1]),
    };

    /// <summary>
    /// Battery Saver drops the display, background work and, on some machines,
    /// the GPU. A threshold of 0 means it never turns itself on.
    /// </summary>
    public static readonly PowerToggle NeverAutoBatterySaver = new()
    {
        Caption = "Never auto-enable Battery Saver",
        Tooltip = "Sets the Battery Saver threshold to 0%, so Windows never turns it\r\n" +
                  "on by itself. You can still enable it by hand from the tray.",
        Settings = new[] { EsBattThreshold },
        OnAc = 0,
        OnDc = 0,
        Describe = v => "threshold " + Rail(v[0]),
    };

    /// <summary>
    /// PCIe Active State Power Management parks the link the discrete GPU sits
    /// behind. Off on DC keeps the dGPU responsive on battery.
    /// </summary>
    public static readonly PowerToggle PcieFullPowerOnBattery = new()
    {
        Caption = "Keep PCIe link at full power on battery",
        Tooltip = "Sets PCI Express ASPM to Off on the battery rail, so the link the\r\n" +
                  "discrete GPU sits behind is not parked. AC is left untouched.",
        Settings = new[] { PcieAspm },
        OnAc = null,
        OnDc = 0,
        Describe = v => "ASPM " + Rail(v[0]),
    };

    public static readonly PowerToggle[] All = { LockCpuBothRails, NeverAutoBatterySaver, PcieFullPowerOnBattery };

    private static string Rail((int? Ac, int? Dc) v) =>
        "AC " + Fmt(v.Ac) + " / DC " + Fmt(v.Dc);

    private static string Fmt(int? v) => v?.ToString() ?? "n/a";

    // --- primitives ---------------------------------------------------------

    /// <summary>GUID of the active scheme, or null if powercfg would not say.</summary>
    public static string? ActiveScheme()
    {
        var r = Shell.Run("powercfg.exe", "/getactivescheme");
        if (r.ExitCode != 0) return null;
        var m = Regex.Match(r.Output, @"([0-9a-fA-F-]{36})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Pulls "Current AC/DC Power Setting Index: 0x..." out of a powercfg
    /// /query dump. Shared with CpuCap so there is one parser, not two.
    /// </summary>
    public static int? ExtractIndex(string text, string rail)
    {
        var m = Regex.Match(text, "Current " + rail + @" Power Setting Index:\s*(0x[0-9a-fA-F]+)");
        if (!m.Success) return null;
        try { return Convert.ToInt32(m.Groups[1].Value, 16); }
        catch { return null; }
    }

    /// <summary>
    /// Settings already unhidden this session. Unhiding is idempotent but costs
    /// a process launch, and Read runs on every dashboard open.
    /// </summary>
    private static readonly HashSet<string> Unhidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Several of these settings ship hidden -- ESBATTTHRESHOLD is hidden on
    /// this machine's Ultimate Performance scheme. For a hidden setting powercfg
    /// prints the scheme header and nothing else, so the value looks absent
    /// rather than erroring, and a write cannot be verified. Clearing
    /// ATTRIB_HIDE makes it readable here and visible in Power Options, which
    /// is the honest outcome: the app should not be changing settings the user
    /// then cannot see.
    /// </summary>
    private static void Unhide(PowerSetting s)
    {
        if (!Unhidden.Add(s.Path)) return;
        Shell.Run("powercfg.exe", "-attributes " + s.Path + " -ATTRIB_HIDE");
    }

    public static (int? Ac, int? Dc) Read(PowerSetting s)
    {
        var v = Query(s);
        if (v.Ac is not null || v.Dc is not null) return v;

        // Nothing came back: either the setting is hidden, or it genuinely does
        // not exist on this scheme. One unhide attempt tells the two apart.
        Unhide(s);
        return Query(s);
    }

    private static (int? Ac, int? Dc) Query(PowerSetting s)
    {
        var r = Shell.Run("powercfg.exe", "/query " + Scheme + " " + s.Path);
        if (r.ExitCode != 0) return (null, null);
        return (ExtractIndex(r.Output, "AC"), ExtractIndex(r.Output, "DC"));
    }

    public static (int? Ac, int? Dc)[] Read(PowerToggle t) =>
        t.Settings.Select(Read).ToArray();

    private static readonly Dictionary<string, bool> SupportCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this scheme has the setting at all. Not every alias exists on
    /// every Windows build: SUB_ENERGYSAVER is absent from every scheme on
    /// Windows 10 19045, so ESBATTTHRESHOLD can be written but never read back.
    ///
    /// That distinction matters twice over. A write to a setting that does not
    /// exist must not be reported as a failed write, and a value that cannot be
    /// read must not be allowed to veto a profile match -- otherwise one absent
    /// setting makes every profile look like "Custom" forever.
    /// </summary>
    public static bool IsSupported(PowerSetting s)
    {
        if (SupportCache.TryGetValue(s.Path, out bool known)) return known;
        var v = Read(s);   // Read already tries an unhide before giving up
        return SupportCache[s.Path] = v.Ac is not null || v.Dc is not null;
    }

    public static bool IsSupported(PowerToggle t) => t.Settings.All(IsSupported);

    /// <summary>
    /// Writes the rails that are non-null and leaves the rest alone. Returns
    /// null on success, or the powercfg complaint.
    /// </summary>
    private static string? Write(PowerSetting s, int? ac, int? dc)
    {
        if (ac is not null)
        {
            var r = Shell.Run("powercfg.exe", "/setacvalueindex " + Scheme + " " + s.Path + " " + ac);
            if (r.ExitCode != 0) return "/setacvalueindex " + s.Path + ": " + FirstLine(r.Output + r.Error);
        }
        if (dc is not null)
        {
            var r = Shell.Run("powercfg.exe", "/setdcvalueindex " + Scheme + " " + s.Path + " " + dc);
            if (r.ExitCode != 0) return "/setdcvalueindex " + s.Path + ": " + FirstLine(r.Output + r.Error);
        }
        return null;
    }

    /// <summary>Without this the scheme keeps the old values in effect.</summary>
    private static void Activate() => Shell.Run("powercfg.exe", "/setactive " + Scheme);

    /// <summary>
    /// Absolute write of one setting, verified. The switch rows go through
    /// Engage/Release instead, which capture and restore; the profile buttons
    /// want to state a value outright. Both build on the same Write, Activate
    /// and read-back pieces rather than each growing their own.
    /// </summary>
    public static WriteResult Set(PowerSetting s, int? ac, int? dc)
    {
        Unhide(s);

        string? err = Write(s, ac, dc);
        if (err != null) return WriteResult.Failure(err);
        Activate();

        var after = Query(s);
        if (ac is not null && after.Ac != ac)
            return WriteResult.Failure(s.Setting + " AC reads " + Fmt(after.Ac) + ", not " + ac + ".");
        if (dc is not null && after.Dc != dc)
            return WriteResult.Failure(s.Setting + " DC reads " + Fmt(after.Dc) + ", not " + dc + ".");

        return WriteResult.Success(s.Setting + " = " + Fmt(ac) + " / " + Fmt(dc) + ".");
    }

    /// <summary>
    /// Engages a toggle. Captures whatever each written rail held first, into
    /// settings.SavedPowerValues, so turning the switch off later restores the
    /// real prior value rather than a guessed default.
    /// </summary>
    public static WriteResult Engage(PowerToggle t, Settings settings)
    {
        foreach (var s in t.Settings)
        {
            Unhide(s);
            var (ac, dc) = Read(s);
            // Capture once. A second engage without an intervening release must
            // not overwrite the original with the value we ourselves wrote.
            if (t.OnAc is not null && ac is not null) settings.SavedPowerValues.TryAdd(s.SavedKey("AC"), ac.Value);
            if (t.OnDc is not null && dc is not null) settings.SavedPowerValues.TryAdd(s.SavedKey("DC"), dc.Value);

            string? err = Write(s, t.OnAc, t.OnDc);
            if (err != null) return WriteResult.Failure(err);
        }

        Activate();
        return Verify(t, t.OnAc, t.OnDc, "On. " + t.Describe(Read(t)) + ".");
    }

    /// <summary>
    /// Restores the captured values. Refuses rather than guessing if nothing
    /// was captured -- writing a made-up default would be worse than leaving
    /// the machine as it is and saying so.
    /// </summary>
    public static WriteResult Release(PowerToggle t, Settings settings)
    {
        foreach (var s in t.Settings)
        {
            int? ac = null, dc = null;
            if (t.OnAc is not null)
            {
                if (!settings.SavedPowerValues.TryGetValue(s.SavedKey("AC"), out int v))
                    return WriteResult.Failure("No saved AC value for " + s.Setting + ". Set it by hand in Power Options.");
                ac = v;
            }
            if (t.OnDc is not null)
            {
                if (!settings.SavedPowerValues.TryGetValue(s.SavedKey("DC"), out int v))
                    return WriteResult.Failure("No saved DC value for " + s.Setting + ". Set it by hand in Power Options.");
                dc = v;
            }

            string? err = Write(s, ac, dc);
            if (err != null) return WriteResult.Failure(err);
        }

        Activate();

        var after = Read(t);
        for (int i = 0; i < t.Settings.Length; i++)
        {
            var s = t.Settings[i];
            if (t.OnAc is not null && settings.SavedPowerValues.TryGetValue(s.SavedKey("AC"), out int wantAc)
                && after[i].Ac != wantAc)
                return WriteResult.Failure("powercfg accepted the write but " + s.Setting + " AC reads " + Fmt(after[i].Ac) + ".");
            if (t.OnDc is not null && settings.SavedPowerValues.TryGetValue(s.SavedKey("DC"), out int wantDc)
                && after[i].Dc != wantDc)
                return WriteResult.Failure("powercfg accepted the write but " + s.Setting + " DC reads " + Fmt(after[i].Dc) + ".");
        }

        // Only forget the originals once they are verified back in place.
        foreach (var s in t.Settings)
        {
            settings.SavedPowerValues.Remove(s.SavedKey("AC"));
            settings.SavedPowerValues.Remove(s.SavedKey("DC"));
        }

        return WriteResult.Success("Off. " + t.Describe(after) + ".");
    }

    private static WriteResult Verify(PowerToggle t, int? wantAc, int? wantDc, string okMessage)
    {
        var after = Read(t);
        for (int i = 0; i < t.Settings.Length; i++)
        {
            if (wantAc is not null && after[i].Ac != wantAc)
                return WriteResult.Failure("powercfg accepted the write but " + t.Settings[i].Setting + " AC reads " + Fmt(after[i].Ac) + ".");
            if (wantDc is not null && after[i].Dc != wantDc)
                return WriteResult.Failure("powercfg accepted the write but " + t.Settings[i].Setting + " DC reads " + Fmt(after[i].Dc) + ".");
        }
        return WriteResult.Success(okMessage);
    }

    /// <summary>True when every written rail already holds the engaged value.</summary>
    public static bool IsEngaged(PowerToggle t, (int? Ac, int? Dc)[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (t.OnAc is not null && values[i].Ac != t.OnAc) return false;
            if (t.OnDc is not null && values[i].Dc != t.OnDc) return false;
        }
        return true;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return line?.Trim() ?? "no output";
    }
}

