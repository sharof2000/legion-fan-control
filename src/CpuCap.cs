using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LegionFanTray;

/// <summary>Shared child-process runner. Used by CpuCap and AutoStart.</summary>
internal static class Shell
{
    public static (int ExitCode, string Output, string Error) Run(string exe, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "could not start " + exe);
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (p.HasExited ? p.ExitCode : -1, o, e);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}

/// <summary>
/// CPU max processor state via powercfg -- a direct port of Set-CpuCap /
/// Reset-CpuCap in scripts/Legion-FanControl.ps1.
///
/// This is heat control at the source rather than fan control: 99% disables
/// Turbo, the main thermal driver on the 5800H. It complements fan forcing
/// instead of competing with it, and touches no Lenovo WMI at all.
/// </summary>
internal static class CpuCap
{
    /// <summary>
    /// 100 means uncapped. 99 is the interesting one (Turbo off); below ~80 is
    /// the floor of diminishing returns.
    /// </summary>
    public static readonly int[] Values = { 100, 99, 95, 90, 85, 80, 75, 70, 60, 50 };

    private const string SettingPath = "SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX";

    /// <summary>Current cap on the active scheme, as (AC, DC) percentages.</summary>
    public static (int? Ac, int? Dc) Read()
    {
        var r = Shell.Run("powercfg.exe", "/query " + SettingPath);
        if (r.ExitCode != 0) return (null, null);
        return (Extract(r.Output, "AC"), Extract(r.Output, "DC"));
    }

    private static int? Extract(string text, string rail)
    {
        var m = Regex.Match(text, "Current " + rail + @" Power Setting Index:\s*(0x[0-9a-fA-F]+)");
        if (!m.Success) return null;
        try { return Convert.ToInt32(m.Groups[1].Value, 16); }
        catch { return null; }
    }

    /// <summary>
    /// Applies to both AC and DC on the active scheme, then verifies by
    /// reading the value back -- powercfg exits 0 even when nothing changed.
    /// </summary>
    public static WriteResult Apply(int percent)
    {
        if (percent < 1 || percent > 100)
            return WriteResult.Failure("Cap must be 1-100 (got " + percent + ").");

        var a = Shell.Run("powercfg.exe", "/setacvalueindex " + SettingPath + " " + percent);
        if (a.ExitCode != 0) return WriteResult.Failure("powercfg /setacvalueindex failed: " + Trim(a.Output + a.Error));

        var d = Shell.Run("powercfg.exe", "/setdcvalueindex " + SettingPath + " " + percent);
        if (d.ExitCode != 0) return WriteResult.Failure("powercfg /setdcvalueindex failed: " + Trim(d.Output + d.Error));

        // Without /setactive the scheme keeps the old value in effect.
        Shell.Run("powercfg.exe", "/setactive SCHEME_CURRENT");

        var (ac, dc) = Read();
        if (ac != percent && dc != percent)
            return WriteResult.Failure("powercfg accepted the write but the cap reads " + Fmt(ac) + " / " + Fmt(dc) + ".");

        string note = percent >= 100
            ? "Cap removed, Turbo re-enabled."
            : "Capped to " + percent + "%. Stored per power scheme - switching plans drops it.";
        return WriteResult.Success(note);
    }

    public static string Describe(int? ac, int? dc) =>
        ac is null && dc is null ? "cap: n/a" : "AC " + Fmt(ac) + "% · DC " + Fmt(dc) + "%";

    private static string Fmt(int? v) => v?.ToString() ?? "n/a";

    private static string Trim(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return line?.Trim() ?? "no output";
    }
}
