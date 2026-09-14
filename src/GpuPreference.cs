using Microsoft.Win32;

namespace LegionFanTray;

/// <summary>
/// Which GPU Windows hands a given executable. This is the registry behind
/// Settings -> System -> Display -> Graphics, nothing more exotic.
///
/// Deliberately generic: the class knows about executables and nothing else.
/// Games, editors, renderers and browsers are all just paths here.
/// </summary>
internal static class GpuPreference
{
    /// <summary>
    /// HKCU, so this follows the account the process was launched from. The app
    /// runs elevated, which means an over-the-shoulder UAC prompt answered with
    /// a *different* admin account would write that account's hive instead.
    /// Not worth a code path on a single-user machine, but that is the rule.
    /// </summary>
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public const int PowerSaving = 1;   // integrated Radeon
    public const int HighPerf = 2;      // discrete NVIDIA

    public static string Describe(int preference) => preference switch
    {
        PowerSaving => "Radeon iGPU",
        HighPerf => "NVIDIA dGPU",
        _ => "preference " + preference,
    };

    private static RegistryKey OpenWrite() =>
        Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
        ?? throw new InvalidOperationException("Could not open " + KeyPath + ".");

    /// <summary>Preference currently recorded for one executable, or null for "let Windows decide".</summary>
    public static int? Read(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return Parse(key?.GetValue(exePath) as string);
        }
        catch { return null; }
    }

    /// <summary>
    /// Everything already under the key, whoever wrote it. The dashboard seeds
    /// its list from this so apps configured in Windows Settings are visible
    /// here instead of being silently shadowed.
    /// </summary>
    public static List<(string Path, int Preference)> ReadAll()
    {
        var found = new List<(string, int)>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return found;
            foreach (string name in key.GetValueNames())
            {
                int? p = Parse(key.GetValue(name) as string);
                if (p is not null && !string.IsNullOrWhiteSpace(name)) found.Add((name, p.Value));
            }
        }
        catch { /* an unreadable key is the same as an empty one here */ }
        return found;
    }

    /// <summary>
    /// The value is "GpuPreference=N;", occasionally with more clauses appended.
    /// N = 0 is Windows' own way of writing "let Windows decide", so it reads
    /// back as no override rather than as a third preference.
    /// </summary>
    private static int? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(kv[1].Trim(), out int v))
                return v == PowerSaving || v == HighPerf ? v : null;
        }
        return null;
    }

    /// <summary>Writes one override and verifies by read-back.</summary>
    public static WriteResult Apply(string exePath, int preference)
    {
        if (preference != PowerSaving && preference != HighPerf)
            return WriteResult.Failure("GPU preference must be 1 or 2 (got " + preference + ").");

        try
        {
            using (var key = OpenWrite())
                key.SetValue(exePath, "GpuPreference=" + preference + ";", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            return WriteResult.Failure("Could not write the GPU preference: " + ex.Message);
        }

        return Read(exePath) == preference
            ? WriteResult.Success(Path.GetFileName(exePath) + " -> " + Describe(preference) + ".")
            : WriteResult.Failure("The registry write did not take for " + Path.GetFileName(exePath) + ".");
    }

    /// <summary>Removes the override, which is what "Let Windows decide" is.</summary>
    public static WriteResult Clear(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(exePath, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            return WriteResult.Failure("Could not clear the GPU preference: " + ex.Message);
        }

        return Read(exePath) is null
            ? WriteResult.Success(Path.GetFileName(exePath) + " -> let Windows decide.")
            : WriteResult.Failure("The override for " + Path.GetFileName(exePath) + " is still present.");
    }

    /// <summary>
    /// Applies every enabled override in one pass, so the master switch is a
    /// single click. Reports the first failure but keeps going, because a
    /// stale path in the list must not block the rest.
    /// </summary>
    public static WriteResult ApplyAll(IEnumerable<GpuOverride> overrides)
    {
        int done = 0;
        string? firstError = null;
        foreach (var o in overrides)
        {
            var r = Apply(o.Path, o.Preference);
            if (r.Ok) done++;
            else firstError ??= r.Message;
        }
        if (firstError != null) return WriteResult.Failure(firstError);
        return WriteResult.Success(done == 0 ? "No apps in the list." : "Applied to " + done + " app" + (done == 1 ? "" : "s") + ".");
    }

    public static WriteResult ClearAll(IEnumerable<GpuOverride> overrides)
    {
        int done = 0;
        string? firstError = null;
        foreach (var o in overrides)
        {
            var r = Clear(o.Path);
            if (r.Ok) done++;
            else firstError ??= r.Message;
        }
        if (firstError != null) return WriteResult.Failure(firstError);
        return WriteResult.Success(done == 0 ? "No apps in the list." : "Cleared " + done + " app" + (done == 1 ? "" : "s") + ".");
    }

}

