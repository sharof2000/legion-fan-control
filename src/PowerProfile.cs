namespace LegionFanTray;

/// <summary>What a profile does to the per-app GPU override list.</summary>
internal enum GpuListAction
{
    /// <summary>Every listed app to the discrete GPU.</summary>
    ToDiscrete,

    /// <summary>Remove every override: let Windows decide.</summary>
    Clear,

    /// <summary>Every listed app to the integrated GPU.</summary>
    ToIntegrated,
}

/// <summary>
/// Everything the profile buttons touch, read in one pass. Serves two jobs: the
/// "what is in force right now" read that decides which button is highlighted,
/// and the one-time capture of how the machine was before any profile was ever
/// applied.
///
/// Every value is nullable, and null means "not readable" rather than zero.
/// Restore skips a null instead of writing a guess -- the same rule
/// PowerPlan.Release follows.
/// </summary>
internal sealed class MachineSnapshot
{
    public int? ProcMinAc { get; set; }
    public int? ProcMinDc { get; set; }
    public int? ProcMaxAc { get; set; }
    public int? ProcMaxDc { get; set; }
    public int? EsBattAc { get; set; }
    public int? EsBattDc { get; set; }
    public int? AspmAc { get; set; }
    public int? AspmDc { get; set; }

    public int? Frtc { get; set; }
    public int? FrtcFps { get; set; }
    public int? Chill { get; set; }
    public int? Boost { get; set; }
    public int? VariBright { get; set; }
    public int? PowerSaverAuto { get; set; }

    /// <summary>The override list as the registry actually holds it, not as settings.json intends it.</summary>
    public List<GpuOverride> GpuOverrides { get; set; } = new();

    public bool GpuOverridesEnabled { get; set; }

    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;

    public static MachineSnapshot Capture(Settings settings)
    {
        var min = PowerPlan.Read(PowerPlan.ProcThrottleMin);
        var max = PowerPlan.Read(PowerPlan.ProcThrottleMax);
        var esb = PowerPlan.Read(PowerPlan.EsBattThreshold);
        var aspm = PowerPlan.Read(PowerPlan.PcieAspm);

        return new MachineSnapshot
        {
            ProcMinAc = min.Ac,
            ProcMinDc = min.Dc,
            ProcMaxAc = max.Ac,
            ProcMaxDc = max.Dc,
            EsBattAc = esb.Ac,
            EsBattDc = esb.Dc,
            AspmAc = aspm.Ac,
            AspmDc = aspm.Dc,

            Frtc = AmdGpu.Read(AmdGpu.FrtcEnabled),
            FrtcFps = AmdGpu.Read(AmdGpu.FrtcMaxFps),
            Chill = AmdGpu.Read(AmdGpu.ChillEnabled),
            Boost = AmdGpu.Read(AmdGpu.BoostEnabled),
            VariBright = AmdGpu.Read(AmdGpu.VariBright),
            PowerSaverAuto = AmdGpu.Read(AmdGpu.PowerSaverAuto),

            GpuOverrides = GpuPreference.ReadAll()
                .Select(x => new GpuOverride { Path = x.Path, Preference = x.Preference })
                .ToList(),
            GpuOverridesEnabled = settings.GpuOverridesEnabled,
        };
    }

    /// <summary>
    /// Puts the machine back to the captured state. Clears the current override
    /// list first: restoring is about the shape the list had, not merging into
    /// whatever it grew into since.
    /// </summary>
    public WriteResult Restore(Settings settings)
    {
        var log = new WriteLog();

        // A null rail was not readable when captured, so there is nothing to
        // put back -- writing a default here would invent state, not restore it.
        log.AddIf(ProcMinAc is not null || ProcMinDc is not null,
            () => PowerPlan.Set(PowerPlan.ProcThrottleMin, ProcMinAc, ProcMinDc));
        log.AddIf(ProcMaxAc is not null || ProcMaxDc is not null,
            () => PowerPlan.Set(PowerPlan.ProcThrottleMax, ProcMaxAc, ProcMaxDc));
        log.AddIf(EsBattAc is not null || EsBattDc is not null,
            () => PowerPlan.Set(PowerPlan.EsBattThreshold, EsBattAc, EsBattDc));
        log.AddIf(AspmAc is not null || AspmDc is not null,
            () => PowerPlan.Set(PowerPlan.PcieAspm, AspmAc, AspmDc));

        log.AddIf(Frtc is not null, () => AmdGpu.ApplyFrtc(Frtc != 0, FrtcFps ?? 60));
        log.AddIf(Chill is not null, () => AmdGpu.Write(AmdGpu.ChillEnabled, Chill!.Value));
        log.AddIf(Boost is not null, () => AmdGpu.Write(AmdGpu.BoostEnabled, Boost!.Value));
        log.AddIf(VariBright is not null, () => AmdGpu.Write(AmdGpu.VariBright, VariBright!.Value));
        log.AddIf(PowerSaverAuto is not null, () => AmdGpu.Write(AmdGpu.PowerSaverAuto, PowerSaverAuto!.Value));

        log.Add(GpuPreference.ClearAll(GpuPreference.ReadAll()
            .Select(x => new GpuOverride { Path = x.Path, Preference = x.Preference })));
        foreach (var o in GpuOverrides) log.Add(GpuPreference.Apply(o.Path, o.Preference));

        settings.GpuOverrides = GpuOverrides.Select(o => new GpuOverride { Path = o.Path, Preference = o.Preference }).ToList();
        settings.GpuOverridesEnabled = GpuOverridesEnabled;

        return log.Summarise("Original state restored");
    }
}

/// <summary>
/// Counts what took and remembers the first thing that did not. A profile that
/// gets ten of thirteen writes done should say which three failed, not abandon
/// the machine half-set and throw.
/// </summary>
internal sealed class WriteLog
{
    private int _ok;
    private int _total;
    private string? _firstError;

    public void Add(WriteResult r)
    {
        _total++;
        if (r.Ok) _ok++;
        else _firstError ??= r.Message;
    }

    /// <summary>Skips a step whose value was never captured, without counting it as a failure.</summary>
    public void AddIf(bool condition, Func<WriteResult> action)
    {
        if (condition) Add(action());
    }

    public WriteResult Summarise(string what) => _firstError is null
        ? WriteResult.Success(what + ". " + _ok + " settings written.")
        : WriteResult.Failure(what + ": " + _ok + " of " + _total + " written. First failure: " + _firstError);
}

/// <summary>
/// One of the three buttons on the Fans tab. Full power turns every
/// power-saving feature off, Balanced restores vendor defaults, Power save
/// turns them all on.
///
/// Fan mode is deliberately absent: the Quiet / Balanced / Performance row
/// directly above already owns the fans, and a profile that silently released
/// fans the user had pinned would be a nasty surprise.
/// </summary>
internal sealed class PowerProfile
{
    public required string Name { get; init; }
    public required string Tooltip { get; init; }

    public required (int Ac, int Dc) ProcMin { get; init; }
    public required (int Ac, int Dc) ProcMax { get; init; }

    /// <summary>Written to both rails: a threshold that differs by rail makes no sense.</summary>
    public required int EsBattThreshold { get; init; }

    /// <summary>DC only, matching the scope of the switch that owns this setting.</summary>
    public required int PcieAspmDc { get; init; }

    public required bool Frtc { get; init; }
    public int FrtcFps { get; init; } = 60;
    public required bool Chill { get; init; }
    public required bool Boost { get; init; }
    public required bool VariBright { get; init; }
    public required bool PowerSaverAuto { get; init; }

    public required GpuListAction Gpu { get; init; }

    /// <summary>Vari-Bright is a level, not a flag: 2 is AMD's default, 0 is off.</summary>
    private int VariBrightLevel => VariBright ? 2 : 0;

    public static readonly PowerProfile FullPower = new()
    {
        Name = "Full power",
        Tooltip = "Every power-saving feature off. CPU pinned to 100% on both rails,\r\n" +
                  "Battery Saver never auto-enables, PCIe link stays awake, no frame\r\n" +
                  "cap, and listed apps run on the discrete GPU.\r\n" +
                  "Does not touch the fans.",
        ProcMin = (100, 100),
        ProcMax = (100, 100),
        EsBattThreshold = 0,
        PcieAspmDc = 0,
        Frtc = false,
        Chill = false,
        Boost = false,
        VariBright = false,
        PowerSaverAuto = false,
        Gpu = GpuListAction.ToDiscrete,
    };

    public static readonly PowerProfile Balanced = new()
    {
        Name = "Balanced",
        Tooltip = "Vendor defaults. Windows manages the CPU normally, Battery Saver\r\n" +
                  "returns to 20%, and the per-app GPU overrides are cleared back to\r\n" +
                  "\"let Windows decide\".\r\n" +
                  "Does not touch the fans.",
        ProcMin = (5, 5),
        ProcMax = (100, 100),
        EsBattThreshold = 20,
        PcieAspmDc = 1,
        Frtc = false,
        Chill = false,
        Boost = true,
        VariBright = true,
        PowerSaverAuto = false,
        Gpu = GpuListAction.Clear,
    };

    public static readonly PowerProfile PowerSave = new()
    {
        Name = "Power save",
        Tooltip = "Every power-saving feature on. CPU capped to 80% on battery, frames\r\n" +
                  "capped at 30, Chill and Boost on, and listed apps moved to the\r\n" +
                  "integrated GPU - which, while a battery FPS cap is in force, is\r\n" +
                  "the faster of the two anyway.\r\n" +
                  "Does not touch the fans.",
        ProcMin = (5, 5),
        ProcMax = (100, 80),
        EsBattThreshold = 20,
        PcieAspmDc = 2,
        Frtc = true,
        FrtcFps = 30,
        Chill = true,
        Boost = true,
        VariBright = true,
        PowerSaverAuto = true,
        Gpu = GpuListAction.ToIntegrated,
    };

    public static readonly PowerProfile[] All = { FullPower, Balanced, PowerSave };

    // --- apply --------------------------------------------------------------

    public WriteResult Apply(Settings settings)
    {
        var log = new WriteLog();

        // Settings this Windows build does not have are skipped outright rather
        // than written and then reported as failures nobody can act on.
        log.AddIf(PowerPlan.IsSupported(PowerPlan.ProcThrottleMin),
            () => PowerPlan.Set(PowerPlan.ProcThrottleMin, ProcMin.Ac, ProcMin.Dc));
        log.AddIf(PowerPlan.IsSupported(PowerPlan.ProcThrottleMax),
            () => PowerPlan.Set(PowerPlan.ProcThrottleMax, ProcMax.Ac, ProcMax.Dc));
        log.AddIf(PowerPlan.IsSupported(PowerPlan.EsBattThreshold),
            () => PowerPlan.Set(PowerPlan.EsBattThreshold, EsBattThreshold, EsBattThreshold));
        log.AddIf(PowerPlan.IsSupported(PowerPlan.PcieAspm),
            () => PowerPlan.Set(PowerPlan.PcieAspm, null, PcieAspmDc));

        // Skipped wholesale rather than reported as failures on a machine with
        // no AMD adapter, where none of these values exist to begin with.
        if (AmdGpu.Available)
        {
            log.Add(AmdGpu.ApplyFrtc(Frtc, FrtcFps));
            log.Add(AmdGpu.Write(AmdGpu.ChillEnabled, Chill ? 1 : 0));
            log.Add(AmdGpu.Write(AmdGpu.BoostEnabled, Boost ? 1 : 0));
            log.Add(AmdGpu.Write(AmdGpu.VariBright, VariBrightLevel));
            log.Add(AmdGpu.Write(AmdGpu.PowerSaverAuto, PowerSaverAuto ? 1 : 0));
        }

        ApplyGpuList(settings, log);

        return log.Summarise(Name + " applied");
    }

    /// <summary>
    /// The stored preference of each entry moves with the write, so the list on
    /// the GPU / Power tab shows what is actually in force rather than a stale
    /// intent from before the profile ran.
    /// </summary>
    private void ApplyGpuList(Settings settings, WriteLog log)
    {
        if (settings.GpuOverrides.Count == 0) return;

        if (Gpu == GpuListAction.Clear)
        {
            log.Add(GpuPreference.ClearAll(settings.GpuOverrides));
            settings.GpuOverridesEnabled = false;
            return;
        }

        int preference = Gpu == GpuListAction.ToDiscrete ? GpuPreference.HighPerf : GpuPreference.PowerSaving;
        foreach (var o in settings.GpuOverrides) o.Preference = preference;
        log.Add(GpuPreference.ApplyAll(settings.GpuOverrides));
        settings.GpuOverridesEnabled = true;
    }

    // --- recognition --------------------------------------------------------

    /// <summary>
    /// Whether the machine is currently in this profile.
    ///
    /// A value that could not be read is "cannot tell", not "does not match" --
    /// it is skipped. Letting one absent setting veto every comparison is what
    /// made the row read "Custom" no matter which button had just been pressed.
    /// A value that *is* readable and disagrees still rules the profile out.
    /// </summary>
    public bool Matches(MachineSnapshot live, Settings settings)
    {
        if (Differs(live.ProcMinAc, ProcMin.Ac) || Differs(live.ProcMinDc, ProcMin.Dc)) return false;
        if (Differs(live.ProcMaxAc, ProcMax.Ac) || Differs(live.ProcMaxDc, ProcMax.Dc)) return false;
        if (Differs(live.EsBattAc, EsBattThreshold) || Differs(live.EsBattDc, EsBattThreshold)) return false;
        if (Differs(live.AspmDc, PcieAspmDc)) return false;

        if (AmdGpu.Available)
        {
            if (DiffersFlag(live.Frtc, Frtc)) return false;
            if (Frtc && Differs(live.FrtcFps, FrtcFps)) return false;
            if (DiffersFlag(live.Chill, Chill)) return false;
            if (DiffersFlag(live.Boost, Boost)) return false;
            if (Differs(live.VariBright, VariBrightLevel)) return false;
            if (DiffersFlag(live.PowerSaverAuto, PowerSaverAuto)) return false;
        }

        return MatchesGpuList(live, settings);
    }

    private static bool Differs(int? live, int expected) => live is not null && live != expected;

    private static bool DiffersFlag(int? live, bool expected) => live is not null && (live != 0) != expected;

    /// <summary>
    /// With an empty list every profile would trivially agree, so the dimension
    /// is treated as "don't care" rather than letting it decide the match.
    /// </summary>
    private bool MatchesGpuList(MachineSnapshot live, Settings settings)
    {
        if (settings.GpuOverrides.Count == 0) return true;

        var byPath = live.GpuOverrides.ToDictionary(o => o.Path, o => o.Preference, StringComparer.OrdinalIgnoreCase);

        foreach (var o in settings.GpuOverrides)
        {
            bool present = byPath.TryGetValue(o.Path, out int actual);
            switch (Gpu)
            {
                case GpuListAction.Clear when present: return false;
                case GpuListAction.ToDiscrete when !present || actual != GpuPreference.HighPerf: return false;
                case GpuListAction.ToIntegrated when !present || actual != GpuPreference.PowerSaving: return false;
            }
        }
        return true;
    }

    /// <summary>The profile in force, or null when the machine matches none of them.</summary>
    public static PowerProfile? Active(MachineSnapshot live, Settings settings) =>
        All.FirstOrDefault(p => p.Matches(live, settings));
}

