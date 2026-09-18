namespace LegionFanTray;

/// <summary>Who last set the fan state. The curve must never undo a human.</summary>
internal enum FanOwner
{
    None,
    Manual,
    Auto,
}

internal sealed class FanSnapshot
{
    public int? CpuC { get; init; }
    public int? GpuC { get; init; }
    public int? Fan0Rpm { get; init; }
    public int? Fan1Rpm { get; init; }
    public uint? SmartFanMode { get; init; }
    public uint? ThermalMode { get; init; }
    public bool? FullSpeed { get; init; }
    public bool OnAc { get; init; }

    /// <summary>Plugged in, but the battery is still draining: a weak charger.</summary>
    public bool LowPowerCharger { get; init; }
    public int MaxRpm { get; init; } = LenovoWmi.FallbackMaxRpm;
    public FanOwner Owner { get; init; }
    public bool AutoMaxEnabled { get; init; }

    /// <summary>SmartFanMode is the oracle for Custom, never ThermalMode.</summary>
    public bool CustomEngaged => SmartFanMode == LenovoWmi.ModeCustom;

    public bool FansPinned => CustomEngaged && FullSpeed == true;

    public string ModeText
    {
        get
        {
            if (FansPinned) return "Custom (fans pinned)";
            if (CustomEngaged) return "Custom (not pinned)";
            return LenovoWmi.ModeName(SmartFanMode);
        }
    }
}

internal sealed record WriteResult(bool Ok, string Message)
{
    public static WriteResult Success(string m) => new(true, m);
    public static WriteResult Failure(string m) => new(false, m);
}

/// <summary>
/// Owns all fan state. Every write is verified by readback, the way
/// Set-FanFullSpeed does in scripts/Legion-FanControl.ps1 -- a bare ReturnValue of 0
/// is never treated as success.
/// </summary>
internal sealed class FanController : IDisposable
{
    private const int RpmDeltaThreshold = 150;

    /// <summary>Idle rate when nothing is watching and no curve is running.</summary>
    private const int IdleIntervalMs = 5000;

    /// <summary>How long auto-max waits after a failed engage before trying again.</summary>
    private static readonly TimeSpan AutoRetryBackoff = TimeSpan.FromSeconds(60);

    private readonly LenovoWmi _wmi;
    private readonly Settings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private FanOwner _owner = FanOwner.None;
    private int _highSamples;
    private int _lowSamples;
    private int _releasedOnce;
    private DateTime _nextAutoAttempt = DateTime.MinValue;

    /// <summary>
    /// Stock mode (1/2/3) that was active before Custom was engaged, so the
    /// release goes back to it. A low-wattage charger locks the EC to Quiet;
    /// asking for Balanced there would only be refused.
    /// </summary>
    private uint? _modeBeforeCustom;

    public FanController(LenovoWmi wmi, Settings settings)
    {
        _wmi = wmi;
        _settings = settings;
    }

    /// <summary>Fires on the poll thread. Subscribers must marshal to the UI.</summary>
    public event Action<FanSnapshot>? SnapshotUpdated;

    /// <summary>Message plus an "is this bad" flag, for balloon tips.</summary>
    public event Action<string, bool>? Notice;

    public FanSnapshot? Latest { get; private set; }

    public FanOwner Owner => _owner;

    /// <summary>Set by the tray when the dashboard is shown or hidden.</summary>
    public bool DashboardVisible { get; set; }

    /// <summary>
    /// The curve counts samples, not seconds, so its timing would shift if the
    /// poll rate changed underneath it. The rate only drops to idle when
    /// nothing is watching AND no curve is running.
    /// </summary>
    private int CurrentIntervalMs =>
        DashboardVisible || _settings.AutoMaxEnabled
            ? _settings.PollIntervalMs
            : Math.Max(_settings.PollIntervalMs, IdleIntervalMs);

    // --- lifecycle ----------------------------------------------------------

    public void Start()
    {
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Layer 5 of the revert chain. If a previous run died holding the fans,
    /// the lock file is still on disk -- put the machine back to Balanced
    /// before doing anything else.
    /// </summary>
    public void RecoverFromDirtyLock()
    {
        try
        {
            if (!File.Exists(AppPaths.LockFile)) return;

            var mode = _wmi.GetSmartFanMode();
            if (mode == LenovoWmi.ModeCustom)
            {
                TryQuiet(() => _wmi.SetFullSpeed(false));
                TryQuiet(() => _wmi.SetSmartFanMode(LenovoWmi.ModeBalanced));
                Notice?.Invoke("Previous session ended without releasing the fans. Restored Balanced.", true);
            }
            ClearLock();
        }
        catch { /* recovery is best effort */ }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = Read();
                Latest = snap;
                SnapshotUpdated?.Invoke(snap);
                await EvaluateCurveAsync(snap, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* never let one bad poll kill the loop */ }

            try { await Task.Delay(CurrentIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private FanSnapshot Read()
    {
        bool onAc = _wmi.IsOnAc();
        return new()
        {
            CpuC = _wmi.GetCpuTemp(),
            GpuC = _wmi.GetGpuTemp(),
            Fan0Rpm = _wmi.GetFanRpm(LenovoWmi.Fan0),
            Fan1Rpm = _wmi.GetFanRpm(LenovoWmi.Fan1),
            SmartFanMode = _wmi.GetSmartFanMode(),
            ThermalMode = _wmi.GetThermalMode(),
            FullSpeed = _wmi.GetFullSpeed(),
            OnAc = onAc,
            LowPowerCharger = onAc && _wmi.IsBatteryDraining(),
            MaxRpm = _wmi.GetMaxRpm(),
            Owner = _owner,
            AutoMaxEnabled = _settings.AutoMaxEnabled,
        };
    }

    // --- the software fan curve ---------------------------------------------

    /// <summary>
    /// Two-point curve with hysteresis, because the firmware refuses
    /// Fan_Set_Table (FanTable_Len is 0 on every FanID x SensorID pair, even
    /// inside Custom Mode). Full-speed toggling is all the hardware offers.
    ///
    /// Only engaging is gated on AC. Once auto owns the fans, the release path
    /// always runs: a flaky power reading must never leave them pinned.
    /// </summary>
    private async Task EvaluateCurveAsync(FanSnapshot snap, CancellationToken ct)
    {
        // The EC left Custom Mode by itself (e.g. charger pulled). Nothing is
        // pinned any more, so stop claiming ownership.
        if (_owner == FanOwner.Auto && snap.SmartFanMode is not null && !snap.CustomEngaged)
        {
            _owner = FanOwner.None;
            _highSamples = 0;
            _lowSamples = 0;
            ClearLock();
            Notice?.Invoke("The firmware left Custom Mode on its own. Auto-max stood down.", false);
            return;
        }

        // A human pinned the fans. Stay out of it until they release.
        if (_owner == FanOwner.Manual) return;

        if (_owner != FanOwner.Auto)
        {
            _lowSamples = 0;
            if (!_settings.AutoMaxEnabled || !snap.OnAc || snap.CpuC is null)
            {
                _highSamples = 0;
                return;
            }

            int cpu = snap.CpuC.Value;
            _highSamples = cpu >= _settings.HighThresholdC ? _highSamples + 1 : 0;
            if (_highSamples >= _settings.EnterSamples && DateTime.UtcNow >= _nextAutoAttempt)
            {
                _highSamples = 0;
                var r = await EngageMaxAsync(FanOwner.Auto, ct).ConfigureAwait(false);
                if (!r.Ok) _nextAutoAttempt = DateTime.UtcNow + AutoRetryBackoff;
                Notice?.Invoke("Auto-max engaged at " + cpu + " C. " + r.Message, !r.Ok);
            }
        }
        else
        {
            // Turning auto-max off while it holds the fans hands them back.
            if (!_settings.AutoMaxEnabled)
            {
                var off = await ReleaseAsync(FanOwner.Auto, ct).ConfigureAwait(false);
                Notice?.Invoke("Auto-max turned off. " + off.Message, !off.Ok);
                return;
            }
            if (snap.CpuC is null) return;

            int cpu = snap.CpuC.Value;
            _highSamples = 0;
            _lowSamples = cpu <= _settings.LowThresholdC ? _lowSamples + 1 : 0;
            if (_lowSamples >= _settings.ExitSamples)
            {
                _lowSamples = 0;
                var r = await ReleaseAsync(FanOwner.Auto, ct).ConfigureAwait(false);
                Notice?.Invoke("Auto-max released at " + cpu + " C. " + r.Message, !r.Ok);
            }
        }
    }

    // --- verified writes ----------------------------------------------------

    /// <summary>
    /// Custom Mode + full speed, the 'custom max' path. Verified twice: the
    /// SmartFanMode readback must be 255, and the fans must actually move.
    /// </summary>
    public async Task<WriteResult> EngageMaxAsync(FanOwner owner, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_wmi.IsOnAc())
                return WriteResult.Failure("On battery. Custom Mode is AC-gated on this firmware - plug in first.");

            int? before0 = _wmi.GetFanRpm(LenovoWmi.Fan0);
            int? before1 = _wmi.GetFanRpm(LenovoWmi.Fan1);

            // Remember where to go back to. Re-engaging from inside Custom
            // must not overwrite the original stock mode.
            var current = _wmi.GetSmartFanMode();
            if (IsStockMode(current)) _modeBeforeCustom = current;

            try { _wmi.SetSmartFanMode(LenovoWmi.ModeCustom); }
            catch (Exception ex) { return WriteResult.Failure("SetSmartFanMode(255) threw: " + ex.Message); }

            await Task.Delay(400, ct).ConfigureAwait(false);

            // SmartFanMode is the oracle. ThermalMode stays at 3 in Custom and
            // checking it here would report failure on a working run.
            if (_wmi.GetSmartFanMode() != LenovoWmi.ModeCustom)
                return WriteResult.Failure("Custom Mode did not engage (SmartFanMode is not 255).");

            MarkLock();

            try { _wmi.SetFullSpeed(true); }
            catch (Exception ex) { return WriteResult.Failure("Fan_Set_FullSpeed(true) threw: " + ex.Message); }

            await Task.Delay(TimeSpan.FromSeconds(_settings.SettleSeconds), ct).ConfigureAwait(false);

            int? after0 = _wmi.GetFanRpm(LenovoWmi.Fan0);
            int? after1 = _wmi.GetFanRpm(LenovoWmi.Fan1);
            int max = _wmi.GetMaxRpm();

            _owner = owner;

            // Either the fans visibly moved, or they are already sitting at the
            // ceiling (re-engaging when they were pinned gives a zero delta).
            bool moved = Delta(before0, after0) > RpmDeltaThreshold || Delta(before1, after1) > RpmDeltaThreshold;
            bool atCeiling = (after0.HasValue && after0.Value >= max - 200) || (after1.HasValue && after1.Value >= max - 200);

            if (moved || atCeiling)
                return WriteResult.Success("Fans at " + Fmt(after0) + " / " + Fmt(after1) + " rpm (ceiling " + max + ").");

            return WriteResult.Failure(
                "Custom Mode engaged but the RPM did not move (" + Fmt(after0) + " / " + Fmt(after1) + "). The EC ignored the write.");
        }
        catch (OperationCanceledException) { return WriteResult.Failure("Cancelled."); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The 'custom off' path, in the script's order: drop full speed first,
    /// then leave Custom Mode, then confirm by readback.
    /// </summary>
    public async Task<WriteResult> ReleaseAsync(FanOwner owner, CancellationToken ct = default)
    {
        // The curve must not release what a human pinned.
        if (owner == FanOwner.Auto && _owner == FanOwner.Manual)
            return WriteResult.Failure("Fans are under manual control. Auto-max left them alone.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try { _wmi.SetFullSpeed(false); }
            catch (Exception ex) { return WriteResult.Failure("Fan_Set_FullSpeed(false) threw: " + ex.Message); }

            uint target = _modeBeforeCustom ?? LenovoWmi.ModeBalanced;
            try { _wmi.SetSmartFanMode(target); }
            catch (Exception ex) { return WriteResult.Failure("SetSmartFanMode(" + target + ") threw: " + ex.Message); }

            await Task.Delay(600, ct).ConfigureAwait(false);

            var mode = _wmi.GetSmartFanMode();
            _owner = FanOwner.None;
            _modeBeforeCustom = null;
            ClearLock();

            // Out of Custom is what matters. The EC may pick a different stock
            // mode than asked for (a low-wattage charger locks Quiet).
            if (mode == target) return WriteResult.Success("Released. Back to " + LenovoWmi.ModeName(mode) + ".");
            if (IsStockMode(mode))
                return WriteResult.Success("Released. Back to " + LenovoWmi.ModeName(mode) +
                    " (the firmware kept " + LenovoWmi.ModeName(mode) + "; a low-wattage charger locks Quiet).");
            return WriteResult.Failure("Released full speed, but the mode reads " + LenovoWmi.ModeName(mode) + ".");
        }
        catch (OperationCanceledException) { return WriteResult.Failure("Cancelled."); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Switch EC curve mode (1/2/3). Leaves Custom Mode cleanly first if it is
    /// engaged, so the fans are never left pinned under a curve mode.
    /// </summary>
    public async Task<WriteResult> SetModeAsync(uint mode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_wmi.GetSmartFanMode() == LenovoWmi.ModeCustom)
            {
                TryQuiet(() => _wmi.SetFullSpeed(false));
                ClearLock();
            }

            try { _wmi.SetSmartFanMode(mode); }
            catch (Exception ex) { return WriteResult.Failure("SetSmartFanMode(" + mode + ") threw: " + ex.Message); }

            await Task.Delay(600, ct).ConfigureAwait(false);
            _owner = FanOwner.None;
            _modeBeforeCustom = null;

            // For modes 1/2/3 ThermalMode IS the oracle: Performance is
            // AC-gated and silently will not take on battery.
            var thermal = _wmi.GetThermalMode();
            if (thermal == mode) return WriteResult.Success(LenovoWmi.ModeName(mode) + " is engaged.");

            if (mode == LenovoWmi.ModePerformance && !_wmi.IsOnAc())
                return WriteResult.Failure("Performance is AC-gated. On battery the EC keeps " + LenovoWmi.ModeName(thermal) + ".");

            return WriteResult.Failure("Mode written but the EC reports " + LenovoWmi.ModeName(thermal) + ".");
        }
        catch (OperationCanceledException) { return WriteResult.Failure("Cancelled."); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Synchronous best-effort release for exit and crash paths. No settle
    /// wait, no verification -- there may be no time. Runs at most once.
    /// </summary>
    public void EmergencyRelease()
    {
        if (Interlocked.Exchange(ref _releasedOnce, 1) != 0) return;
        try
        {
            if (_wmi.GetSmartFanMode() != LenovoWmi.ModeCustom)
            {
                ClearLock();
                return;
            }
            TryQuiet(() => _wmi.SetFullSpeed(false));
            TryQuiet(() => _wmi.SetSmartFanMode(LenovoWmi.ModeBalanced));
            ClearLock();
        }
        catch { /* nothing useful to do this late */ }
    }

    // --- helpers ------------------------------------------------------------

    private static bool IsStockMode(uint? mode) =>
        mode is LenovoWmi.ModeQuiet or LenovoWmi.ModeBalanced or LenovoWmi.ModePerformance;

    private static int Delta(int? a, int? b) =>
        a.HasValue && b.HasValue ? Math.Abs(b.Value - a.Value) : 0;

    private static string Fmt(int? v) => v?.ToString() ?? "n/a";

    private static void TryQuiet(Action a)
    {
        try { a(); } catch { /* ignored */ }
    }

    private static void MarkLock()
    {
        try
        {
            AppPaths.EnsureDir();
            File.WriteAllText(AppPaths.LockFile, "custom mode engaged by pid " + Environment.ProcessId);
        }
        catch { /* the lock file is a safety net, not a hard dependency */ }
    }

    private static void ClearLock()
    {
        try { if (File.Exists(AppPaths.LockFile)) File.Delete(AppPaths.LockFile); }
        catch { /* ignored */ }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _gate.Dispose();
    }
}
