using Microsoft.Win32;

namespace LegionFanTray;

/// <summary>One switch row on the AMD tab: a single driver DWORD, on or off.</summary>
internal sealed class AmdToggle
{
    public required string Caption { get; init; }
    public required string Tooltip { get; init; }
    public required string ValueName { get; init; }

    /// <summary>
    /// Written when the pill goes on / off. Most of these are 1 and 0;
    /// Vari-Bright is a level, where 2 is AMD's default and 0 is off.
    /// </summary>
    public int OnValue { get; init; } = 1;

    public int OffValue { get; init; }

    /// <summary>Anything other than OffValue counts as on, so an unexpected level still reads true.</summary>
    public bool IsOn(int? value) => value is not null && value != OffValue;
}

/// <summary>
/// AMD's counterparts to the NVIDIA settings the app deliberately will not
/// touch. The difference is that these are plain REG_DWORDs in the display
/// adapter's driver key rather than entries in a binary profile database, so
/// they can be read, written and verified like any other setting.
///
/// Two limits worth knowing, both inherent rather than incidental:
///   * These are global. AMD's per-application profiles live in
///     %LOCALAPPDATA%\AMD\CN\gmdb.blb, which is binary and undocumented -- the
///     same wall as NVIDIA, so per-app AMD control is out of scope.
///   * They take effect when the capped application next starts, not
///     immediately, and Adrenalin may rewrite them when its own UI is opened.
///     Hence the read-back on every refresh rather than trusting our last write.
/// </summary>
internal static class AmdGpu
{
    private const string ClassRoot =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Adrenalin's own copy of the Power Saver flag, outside the driver key.</summary>
    private const string CnKey = @"SOFTWARE\AMD\CN";

    // --- the values ---------------------------------------------------------

    public const string FrtcEnabled = "KMD_FRTCInitialStatus";
    public const string FrtcMaxFps = "KMD_MaxFrameRateRequested";
    public const string ChillEnabled = "KMD_ChillEnabled";
    public const string BoostEnabled = "KMD_RadeonBoostEnabled";
    public const string VariBright = "PP_UserVariBrightLevel";
    public const string PowerSaverAuto = "PowerSaverAutoEnable_DEF";

    /// <summary>
    /// Frame Rate Target Control. This is the closest thing AMD has to Battery
    /// Boost: a global cap the driver applies without telling anyone. Measured
    /// on this machine it was on, at 60.
    /// </summary>
    public static readonly AmdToggle Frtc = new()
    {
        Caption = "Frame rate cap (FRTC)",
        Tooltip = "Frame Rate Target Control: a global driver frame cap.\r\n" +
                  "AMD removed it from the Adrenalin UI but left it in the driver,\r\n" +
                  "so the registry is the only place it can be seen or changed.\r\n" +
                  "Takes effect the next time the capped application starts.",
        ValueName = FrtcEnabled,
    };

    public static readonly AmdToggle Chill = new()
    {
        Caption = "Radeon Chill",
        Tooltip = "Drops the frame rate while the scene is still and raises it\r\n" +
                  "again on input. Saves power at the cost of latency.",
        ValueName = ChillEnabled,
    };

    public static readonly AmdToggle Boost = new()
    {
        Caption = "Radeon Boost",
        Tooltip = "Lowers render resolution during fast motion to hold frame rate.\r\n" +
                  "Trades image quality for frames.",
        ValueName = BoostEnabled,
    };

    /// <summary>
    /// The one that changes what you see rather than what you measure: it drops
    /// maximum panel brightness on battery, silently.
    /// </summary>
    public static readonly AmdToggle VariBrightToggle = new()
    {
        Caption = "Vari-Bright (dims the panel on battery)",
        Tooltip = "Reduces maximum display brightness when on battery, without\r\n" +
                  "reporting it. Off restores full brightness; on returns the\r\n" +
                  "level to AMD's default of 2.",
        ValueName = VariBright,
        OnValue = 2,
    };

    public static readonly AmdToggle PowerSaver = new()
    {
        Caption = "Adrenalin Power Saver auto-enable",
        Tooltip = "Lets AMD Software turn its Power Saver profile on by itself.\r\n" +
                  "Written to both the driver key and Adrenalin's own copy.",
        ValueName = PowerSaverAuto,
    };

    public static readonly AmdToggle[] All = { Frtc, Chill, Boost, VariBrightToggle, PowerSaver };

    /// <summary>Cap values offered by the dropdown. 0 is not one: that is what the switch is for.</summary>
    public static readonly int[] FpsValues = { 30, 40, 45, 60, 72, 90, 120, 144, 165, 240 };

    // --- adapter lookup -----------------------------------------------------

    private static string? _cachedKey;
    private static bool _searched;

    /// <summary>
    /// The AMD adapter's driver key, e.g. ...\Class\{4d36e968-...}\0001. The
    /// index is not fixed -- on this machine the NVIDIA adapter holds 0000 --
    /// so the subkeys are matched on DriverDesc rather than assumed.
    /// </summary>
    public static string? DriverKey()
    {
        if (_searched) return _cachedKey;
        _searched = true;

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ClassRoot);
            if (root is null) return _cachedKey = null;

            foreach (string name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub?.GetValue("DriverDesc") is not string desc) continue;
                if (desc.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) < 0
                    && desc.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) < 0) continue;

                _cachedName = desc;
                return _cachedKey = ClassRoot + "\\" + name;
            }
        }
        catch { /* an unreadable class key is the same as no AMD adapter here */ }

        return _cachedKey = null;
    }

    private static string? _cachedName;

    /// <summary>DriverDesc of the matched adapter, for the status line.</summary>
    public static string? AdapterName()
    {
        DriverKey();
        return _cachedName;
    }

    public static bool Available => DriverKey() is not null;

    // --- read / write -------------------------------------------------------

    public static int? Read(string valueName)
    {
        string? key = DriverKey();
        if (key is null) return null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(valueName) is int v ? v : null;
        }
        catch { return null; }
    }

    public static int? Read(AmdToggle t) => Read(t.ValueName);

    /// <summary>
    /// Writes a driver DWORD and verifies by read-back. Needs admin, which the
    /// process already has -- without it the write throws rather than silently
    /// doing nothing, so a failure is always reported.
    /// </summary>
    public static WriteResult Write(string valueName, int value)
    {
        string? key = DriverKey();
        if (key is null) return WriteResult.Failure("No AMD display adapter found.");

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key, writable: true);
            if (k is null) return WriteResult.Failure("Could not open the AMD driver key for writing.");
            k.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            return WriteResult.Failure("Could not write " + valueName + ": " + ex.Message);
        }

        // Adrenalin keeps its own copy of this one and will otherwise put the
        // driver key back the next time its UI runs.
        if (valueName == PowerSaverAuto) MirrorPowerSaverToAdrenalin(value);

        int? after = Read(valueName);
        return after == value
            ? WriteResult.Success(valueName + " = " + value + ". Restart the affected app for it to take effect.")
            : WriteResult.Failure("The write did not take: " + valueName + " reads " + (after?.ToString() ?? "n/a") + ".");
    }

    public static WriteResult Write(AmdToggle t, bool on) => Write(t.ValueName, on ? t.OnValue : t.OffValue);

    private static void MirrorPowerSaverToAdrenalin(int value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(CnKey, writable: true);
            k?.SetValue("PowerSaverAutoEnable_CUR", value, RegistryValueKind.DWord);
        }
        catch { /* best effort: the driver key is the one that decides */ }
    }

    /// <summary>
    /// Turns the cap on at a chosen rate, or off. Two values move together --
    /// enabling FRTC without a rate would cap at whatever was last requested.
    /// </summary>
    public static WriteResult ApplyFrtc(bool on, int fps)
    {
        if (on)
        {
            var r = Write(FrtcMaxFps, fps);
            if (!r.Ok) return r;
        }
        return Write(FrtcEnabled, on ? 1 : 0);
    }

    /// <summary>Live value of the cap, whether or not the cap is currently on.</summary>
    public static int? ReadFrtcFps() => Read(FrtcMaxFps);
}

/// <summary>Opening AMD's own front-end, for the settings that only it exposes.</summary>
internal static class AmdHints
{
    private static readonly string[] Candidates =
    {
        @"AMD\CNext\CNext\RadeonSoftware.exe",
        @"AMD\CNext\CNext\cnext.exe",
    };

    public static WriteResult LaunchAdrenalin() => VendorApp.LaunchFirst(Candidates,
        "AMD Software was not found under Program Files. Open it from the Start menu.");
}

