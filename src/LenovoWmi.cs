using System.Management;

namespace LegionFanTray;

/// <summary>
/// Direct port of the WMI calls proven correct on this machine by
/// scripts/Legion-FanControl.ps1 (BIOS HACN46WW, Legion S7-15ACH6 / 82K8).
///
/// Every quirk encoded here was measured on hardware, not read out of a
/// datasheet. Read docs/findings.md before changing anything.
/// </summary>
internal sealed class LenovoWmi : IDisposable
{
    private const string WmiScope = @"\\.\root\WMI";
    private const string CimScope = @"\\.\root\CIMV2";

    public const string GamezoneClass = "LENOVO_GAMEZONE_DATA";
    public const string FanMethodClass = "LENOVO_FAN_METHOD";
    public const string FanTableClass = "LENOVO_FAN_TABLE_DATA";

    public const uint ModeQuiet = 1;
    public const uint ModeBalanced = 2;
    public const uint ModePerformance = 3;
    public const uint ModeCustom = 255;

    // Only these IDs are valid here. Everything else throws "Invalid object".
    public const byte SensorCpu = 3;
    public const byte SensorGpu = 4;
    public const byte Fan0 = 0;
    public const byte Fan1 = 1;

    /// <summary>Fallback ceiling if LENOVO_FAN_TABLE_DATA cannot be read.</summary>
    public const int FallbackMaxRpm = 4300;

    private readonly Dictionary<string, ManagementObject> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    // --- instance plumbing --------------------------------------------------

    private ManagementObject GetInstance(string className)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(className, out var cached)) return cached;

            var scope = new ManagementScope(WmiScope);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + className));
            foreach (ManagementBaseObject o in searcher.Get())
            {
                var mo = (ManagementObject)o;
                _cache[className] = mo;
                return mo;
            }
            throw new ManagementException("No instance of " + className + @" in root\WMI.");
        }
    }

    private void Evict(string className)
    {
        lock (_sync)
        {
            if (_cache.Remove(className, out var mo)) mo.Dispose();
        }
    }

    /// <summary>
    /// Invoke a method, retrying once against a freshly-acquired instance.
    /// A cached ManagementObject can go stale (WMI restart, provider reload).
    /// </summary>
    private ManagementBaseObject? Invoke(string className, string method, IDictionary<string, object>? args)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var obj = GetInstance(className);
                ManagementBaseObject? inParams = null;
                if (args is { Count: > 0 })
                {
                    inParams = obj.GetMethodParameters(method);
                    foreach (var kv in args) inParams[kv.Key] = kv.Value;
                }
                return obj.InvokeMethod(method, inParams, null);
            }
            catch when (attempt == 0)
            {
                Evict(className);
            }
        }
    }

    private static int? ToInt(object? v)
    {
        if (v is null) return null;
        try { return Convert.ToInt32(v); }
        catch { return null; }
    }

    // --- reads. Each is individually guarded: one throwing sensor must never
    //     kill the poll loop. Failure degrades to null, shown as "n/a". ------

    /// <summary>
    /// NOTE: the value comes back in .CurrentSensorTemperature, NOT .Data.
    /// GetCPUTemp / GetGPUTemp on LENOVO_GAMEZONE_DATA return 0 on this BIOS
    /// and are deliberately not used.
    /// </summary>
    public int? GetSensorTemp(byte sensorId)
    {
        try
        {
            var r = Invoke(FanMethodClass, "Fan_GetCurrentSensorTemperature",
                new Dictionary<string, object> { ["SensorID"] = sensorId });
            var v = ToInt(r?["CurrentSensorTemperature"]);
            return v is > 0 ? v : null;
        }
        catch { return null; }
    }

    public int? GetCpuTemp() => GetSensorTemp(SensorCpu);

    public int? GetGpuTemp() => GetSensorTemp(SensorGpu);

    /// <summary>NOTE: value is in .CurrentFanSpeed, NOT .Data.</summary>
    public int? GetFanRpm(byte fanId)
    {
        try
        {
            var r = Invoke(FanMethodClass, "Fan_GetCurrentFanSpeed",
                new Dictionary<string, object> { ["FanID"] = fanId });
            return ToInt(r?["CurrentFanSpeed"]);
        }
        catch { return null; }
    }

    public bool? GetFullSpeed()
    {
        try
        {
            var r = Invoke(FanMethodClass, "Fan_Get_FullSpeed", null);
            var v = r?["Status"];
            if (v is null) return null;
            if (v is bool b) return b;
            var i = ToInt(v);
            return i.HasValue ? i.Value != 0 : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The ORACLE for Custom Mode: returns 255 when Custom is engaged.
    /// GetThermalMode does NOT follow into Custom and must not be used for it.
    /// </summary>
    public uint? GetSmartFanMode()
    {
        try
        {
            var r = Invoke(GamezoneClass, "GetSmartFanMode", null);
            var v = ToInt(r?["Data"]);
            return v.HasValue ? (uint)v.Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Effective EC profile. Correct oracle for modes 1/2/3 (Performance is
    /// AC-gated and will not take on battery), but stays at 3 inside Custom.
    /// </summary>
    public uint? GetThermalMode()
    {
        try
        {
            var r = Invoke(GamezoneClass, "GetThermalMode", null);
            var v = ToInt(r?["Data"]);
            return v.HasValue ? (uint)v.Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Fan ceiling. Fan_Get_MaxSpeed returns null out-params on this BIOS, so
    /// read the property off LENOVO_FAN_TABLE_DATA instead.
    /// </summary>
    public int GetMaxRpm()
    {
        try
        {
            var scope = new ManagementScope(WmiScope);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + FanTableClass));
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = o;
                var v = ToInt(mo["CurrentFanMaxSpeed"]);
                if (v is > 0) return v.Value;
            }
        }
        catch { /* fall through to the measured default */ }
        return FallbackMaxRpm;
    }

    /// <summary>
    /// Custom Mode and Performance are AC-gated. Asks Windows whether a charger
    /// is connected (ACLineStatus), not what the battery is doing: a
    /// low-wattage charger that cannot keep up leaves BatteryStatus at 1
    /// (discharging) while still plugged in, and the firmware still accepts
    /// Custom Mode there.
    /// </summary>
    public bool IsOnAc()
    {
        try
        {
            switch (SystemInformation.PowerStatus.PowerLineStatus)
            {
                case PowerLineStatus.Online: return true;
                case PowerLineStatus.Offline: return false;
            }
        }
        catch { /* fall back to Win32_Battery */ }

        // 2 = on AC, 3 = fully charged, 6-9 = charging. No battery instance at
        // all means treat as AC.
        var statuses = GetBatteryStatuses();
        if (statuses is null || statuses.Count == 0) return true;
        return statuses.Any(s => s is 2 or 3 or (>= 6 and <= 9));
    }

    /// <summary>
    /// True when a battery reports discharging (BatteryStatus 1). Together with
    /// IsOnAc that identifies a charger too weak to cover the load.
    /// </summary>
    public bool IsBatteryDraining() =>
        GetBatteryStatuses()?.Contains(1) == true;

    private static List<int>? GetBatteryStatuses()
    {
        try
        {
            var scope = new ManagementScope(CimScope);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT BatteryStatus FROM Win32_Battery"));
            var list = new List<int>();
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using var mo = o;
                if (ToInt(mo["BatteryStatus"]) is int s) list.Add(s);
            }
            return list;
        }
        catch { return null; }
    }

    // --- writes. These throw on failure. The caller verifies by readback and
    //     never treats a bare ReturnValue == 0 as success. -------------------

    public void SetFullSpeed(bool on) =>
        Invoke(FanMethodClass, "Fan_Set_FullSpeed", new Dictionary<string, object> { ["Status"] = on });

    /// <summary>
    /// The only strategy that engages Custom Mode on this BIOS.
    /// LENOVO_OTHER_METHOD.Set_Custom_Mode_Status(1) returns success and does
    /// nothing, so it is deliberately not implemented.
    /// </summary>
    public void SetSmartFanMode(uint mode) =>
        Invoke(GamezoneClass, "SetSmartFanMode", new Dictionary<string, object> { ["Data"] = mode });

    public static string ModeName(uint? mode) => mode switch
    {
        ModeQuiet => "Quiet",
        ModeBalanced => "Balanced",
        ModePerformance => "Performance",
        ModeCustom => "Custom",
        null => "n/a",
        _ => "Unknown (" + mode + ")",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            foreach (var mo in _cache.Values) mo.Dispose();
            _cache.Clear();
        }
    }
}
