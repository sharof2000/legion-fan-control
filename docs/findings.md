# Lenovo Legion S7-15ACH6 — Fan Control: SOLVED

**System:** Lenovo Legion S7-15ACH6 — Model `82K8`, SKU `LENOVO_MT_82K8_BU_idea_FM_Legion S7 15ACH6`
**CPU/GPU:** AMD Ryzen 7 5800H + NVIDIA RTX, 32 GB RAM, Windows 10 19045
**BIOS:** `HACN46WW`, released 2024-11-14
**Status:** Fan forcing **works**. Confirmed on hardware 2026-08-24.

---

## The answer

From an **elevated** terminal, on **AC power**:

```powershell
.\Legion-FanControl.ps1 custom max     # 2800 -> 4300 rpm, both fans
.\Legion-FanControl.ps1 custom off     # release, back to Balanced
```

Measured: Fan0 2800 → 4300 rpm, Fan1 2700 → 4300 rpm after an 8 s settle. 4300 is `CurrentFanMaxSpeed` — the fans are at the firmware ceiling.

**Always run `custom off` when you're done.** In Custom Mode the host owns the fans; nothing will bring them back down on its own.

---

## Why Performance mode felt broken

Nothing was broken. Three separate misreadings stacked up:

1. **Performance mode is a curve, not full speed.** `mode performance` correctly sets `SmartFanMode = 3` and `ThermalMode = 3`. The EC then holds ~2800–3800 rpm until temperature demands more. It was working as designed — it just isn't a fan-max button.
2. **`Fan_Set_FullSpeed` is gated behind Custom Mode.** In modes 1/2/3 the EC owns the curve and silently drops host fan writes. That is the entire explanation for the "returns success, does nothing" behaviour.
3. **The original tool used 2 of the ~14 Lenovo WMI classes**, and never touched `LENOVO_OTHER_METHOD` — where the Custom Mode capability is advertised.

Lenovo Vantage, the prime suspect going in, turned out to be **irrelevant**: it was running throughout (`LenovoVantageService`, `ImControllerService`, `LenovoGamingSystemAddin`) and did not interfere. `-SuspendVantage` exists in the tool but was not needed.

---

## Measured facts (BIOS HACN46WW, 2026-08-24)

### Custom Mode was advertised all along

```
GetSupportThermalMode = 65543 = 0x10007
  bit 0  Quiet
  bit 1  Balanced
  bit 2  Performance
  bit 16 CUSTOM          <- always there, never queried until now
GetCustomModeAbility  = 63 = 0x3F
Get_Legion_Device_Support_Feature = 24 = 0x18
GetDeviceType         = CpuType 1, GpuType 3
```

### Only one unlock strategy works

| Strategy | Result |
|---|---|
| `LENOVO_OTHER_METHOD.Set_Custom_Mode_Status(1)` | **No effect.** Returns success, mode stays 3. |
| `LENOVO_GAMEZONE_DATA.SetSmartFanMode(255)` | **Engages Custom Mode.** |

The tool tries them in order and stops at the first that takes, which is why this was caught rather than being written off after the first failure.

### `ThermalMode` is NOT the oracle for Custom Mode

Inside Custom Mode: `SmartFanMode = 255`, but `ThermalMode` stays at `3`. Any check that requires `ThermalMode` to follow will wrongly report failure. **`SmartFanMode` is the oracle for Custom; `ThermalMode` remains the oracle for modes 1/2/3.**

### `Fan_Set_Table` is genuinely unusable here

`FanTable_Len = 0` on every one of the 18 `FanID × SensorID` combinations, **including inside Custom Mode**. `LENOVO_FAN_TABLE_DATA` reports `FanTable_Data = null`, `SensorTable_Len = 0` for both fan instances, and `LENOVO_FAN_MAX_SPEED_DATA` returns "Not supported".

So Round 1's observation about the empty curve table was correct — but the conclusion drawn from it ("the firmware refuses fan control") was not. **No custom fan curve on this BIOS. Full-speed toggle only.** LegionFanControl's "Fan Curve Table not found. Incompatible laptop." was accurate about the table and misleading about the machine.

### Methods that return "Invalid object" on this BIOS

`GetThermalTableID`, `GetFanCount`, `GetFanMaxSpeed`, `GetFan1Speed`, `GetFan2Speed`, `GetTriggerTemperatureValue`, `Get_Device_Current_Support_Feature`, `Fan_GetCurrentFanSpeed(FanID=2)`, `Fan_GetCurrentSensorTemperature` for SensorID 0/1/2/5.

Working substitutes: fan RPM via `Fan_GetCurrentFanSpeed` FanID **0 and 1 only**; temps via `Fan_GetCurrentSensorTemperature` SensorID **3 (CPU) and 4 (GPU) only**. `Fan_Get_MaxSpeed` returns null out-params for every fan — use `LENOVO_FAN_TABLE_DATA.CurrentFanMaxSpeed` (4300) / `DefaultFanMaxSpeed` (4400) instead.

Return-value gotchas that cost time in Round 1: `Fan_GetCurrentFanSpeed` returns `.CurrentFanSpeed`, `Fan_GetCurrentSensorTemperature` returns `.CurrentSensorTemperature` — **not** `.Data`. `GetCPUTemp` / `GetGPUTemp` return 0 and are useless.

### Elevation

Unelevated CIM calls into these classes return `Access denied` for **reads as well as writes**. Any test from a non-admin shell reports failure regardless of what the firmware would have done. Round 1 did not record this.

---

## Corrections to Round 1 (19 June 2026)

| Round 1 claim | Verdict |
|---|---|
| "`SetSmartFanMode` no-ops — mode never changed" | **Wrong.** Works on AC. Round 1 tested on battery, where Performance is firmware-gated. |
| "`FanTable_Len = 0` means the BIOS has no curve table" | **Right observation, wrong inference.** True even in Custom Mode — but it says nothing about the full-speed toggle. |
| "The EC gates writes behind a signed control-owner handshake only Vantage holds" | **Wrong.** Inferred from the mere existence of `SetDDSControlOwner`/`SetLightControlOwner`. Plain `Invoke-CimMethod` moves the fans once Custom Mode is engaged. |
| "You cannot force the fans from raw PowerShell on this firmware" | **Wrong.** You can. |
| "Every write method silently no-ops" | **Wrong.** They no-op in the *wrong mode*. |

The doc and the script contradicted each other for two months on point 1 — the script's header was right, this doc was stale. Fixed.

---

## The tool

`Legion-FanControl.ps1`, all write paths verified by readback (a bare `ReturnValue = 0` is never treated as success):

| Command | Notes |
|---|---|
| `custom max` | Unlock Custom Mode + pin fans, one step. **The one that works.** |
| `custom off` | Release fans, restore Balanced. Always run this. |
| `custom status` | Mode, full-speed flag, readable fan tables. |
| `custom on` / `custom fullspeed <on\|off>` | The two halves of `custom max`, separately. |
| `custom curve [dump\|-N]` | No-op on HACN46WW (no table). Kept for other models. |
| `probe` | Read-only capability dump → timestamped `probe-*.txt`. |
| `monitor` | Live dashboard; shows a warning while Custom Mode is active. |
| `mode <q\|b\|p>` | EC curve modes. Performance is AC-only and will not pin the fans. |
| `force <on\|off>` | Raw `Fan_Set_FullSpeed` outside Custom Mode. Expected to no-op. |
| `cap [percent]` / `uncap` | `powercfg PROCTHROTTLEMAX`. Heat at the source. |
| `-SuspendVantage` | Stops Vantage services around a write, restores in `finally`. Not needed on this machine, but keeps the diagnostic available. |

---

## Not needed, but available

- **Direct EC access** (RWEverything / NBFC / `johnfanv2/LenovoLegionLinux` offsets) — was the fallback plan. Unnecessary: the WMI route works. Don't take the risk.
- **LenovoLegionToolkit** — unnecessary for fan forcing now, but still the reference implementation if a fan *curve* ever matters on another machine.
- **`LENOVO_CPU_METHOD` / `LENOVO_GPU_METHOD`** — `CPU_Set_LongTerm/ShortTerm_PowerLimit`, `GPU_Set_cTGP/PPAB_PowerLimit`. The vendor's own PL1/PL2 and GPU TGP interface, strictly better than RyzenAdj. Unexplored; the obvious next lever if 4300 rpm still isn't enough.
- **`cap 99`** — disables Turbo, the main thermal driver on a 5800H. Complements fan forcing rather than competing with it. Stored per power scheme, so switching plans drops it.
- **Physical** — cooling pad, repaste. At 77 °C CPU while sitting at 2800 rpm, this machine has thermal headroom problems worth addressing at the hardware level too.

---

## Appendix — minimal reproduction

Elevated shell, AC power:

```powershell
$g = Get-CimInstance -Namespace root\WMI -ClassName LENOVO_GAMEZONE_DATA
$f = Get-CimInstance -Namespace root\WMI -ClassName LENOVO_FAN_METHOD

# 1. Enter Custom Mode. SmartFanMode goes to 255; ThermalMode does NOT follow.
$g | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]255 }

# 2. Pin the fans.
$f | Invoke-CimMethod -MethodName Fan_Set_FullSpeed -Arguments @{ Status = $true }

# 3. Verify (wait ~8 s). Expect 4300 on both.
$f | Invoke-CimMethod -MethodName Fan_GetCurrentFanSpeed -Arguments @{ FanID = [byte]0 }
$f | Invoke-CimMethod -MethodName Fan_GetCurrentFanSpeed -Arguments @{ FanID = [byte]1 }

# 4. RELEASE — do not skip this.
$f | Invoke-CimMethod -MethodName Fan_Set_FullSpeed -Arguments @{ Status = $false }
$g | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]2 }
```

---

## History

- **19 June 2026** — Round 1. Concluded the BIOS refuses all fan writes behind a signed control-owner handshake. **Superseded; the conclusion was wrong.**
- **24 August 2026** — Round 2. Identified Custom Mode as the gate, added `probe` / `custom` / `-SuspendVantage`, and **confirmed on hardware: 2800 → 4300 rpm**. Vantage was not the blocker. `Fan_Set_Table` confirmed unusable on this BIOS.
