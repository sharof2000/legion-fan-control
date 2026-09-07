# Legion Fan Tray — Design Document

**Status:** Draft for discussion. No code written yet.
**Date:** 2026-08-24
**Target:** Lenovo Legion S7-15ACH6 (82K8), BIOS HACN46WW. Windows 10 and 11.
**Companion:** [`findings.md`](findings.md) — the hardware investigation this app is built on.

## Context

`Legion-FanControl.ps1` now works: `custom max` engages Custom Mode via `SetSmartFanMode(255)` and pins both fans at 4300 rpm on BIOS `HACN46WW`. But it's a CLI that needs an elevated PowerShell window every time, and there's no at-a-glance view of temps.

The goal is a small Windows tray app that makes the working path a two-click affair: lives in the notification area next to the clock, double-click opens a live temp/RPM readout, and buttons fire the same actions the script exposes. Plus one thing the CLI can't do — since the firmware refuses `Fan_Set_Table`, the app can implement a **software fan curve** by polling temperature and toggling full-speed itself.

This document is the design only. No code is written yet.

---

## Language: C# / .NET 8 WinForms

**Recommended, and already installed here** — `dotnet --list-sdks` shows 8.0.408 and 8.0.424, with `Microsoft.WindowsDesktop.App 8.0.30`. Zero toolchain setup.

Why it wins for this specific app:

- `NotifyIcon` is a first-class WinForms primitive — tray icon, tooltip, balloon notifications, context menu, double-click event, all built in. This is the single biggest factor.
- WMI is native via the `System.Management` NuGet package. `ManagementObject.InvokeMethod` maps 1:1 onto the `Invoke-CimMethod` calls the script already makes — this is a direct port, not a reimplementation.
- Publishes as a **single self-contained .exe**, no .NET install required on the target, no PowerShell dependency.
- `app.manifest` gives declarative UAC elevation.
- GDI+ (`System.Drawing`) draws the gauges with no third-party UI library.

Rejected: **PowerShell + WinForms** (console-window hiding hacks, slow startup, awkward to ship as an exe); **Python + pystray** (needs a runtime, WMI via pywin32 is clumsier, and the 32-bit Python 3.11 on this box would be a poor fit for a 64-bit WMI provider); **Rust/Go** (WMI bindings are painful and there is no good tray story); **C++/Win32** (all the cost, none of the benefit).

WPF is the main alternative to WinForms and would give nicer gauge rendering, but it still needs `NotifyIcon` from WinForms interop or a third-party package. Not worth it for a window this small.

---

## Architecture

```
fan-control/
  Legion-FanControl.ps1              # unchanged — stays the CLI + reference impl
  legion-s7-fan-control-findings.md  # unchanged
  app/
    LegionFanTray.csproj
    app.manifest                     # requireAdministrator
    Program.cs                       # entry, single-instance mutex, crash guards
    LenovoWmi.cs                     # the ported WMI layer  <- core
    FanController.cs                 # state machine, safety, auto-max curve
    TrayApp.cs                       # NotifyIcon + context menu
    DashboardForm.cs                 # the double-click window
    GaugeControl.cs                  # GDI+ arc gauge
    Settings.cs                      # JSON persistence
    AutoStart.cs                     # Task Scheduler registration
```

`LenovoWmi.cs` is a **native C# port** of the calls proven in the script — no shelling out to PowerShell. Reads land in ~5 ms, so 1-second polling is cheap, and there's no console flash.

---

## The WMI layer (`LenovoWmi.cs`)

Direct port of what the probe confirmed working. Everything hangs off `\\.\root\WMI`.

```csharp
var scope = new ManagementScope(@"\\.\root\WMI");
using var s = new ManagementObjectSearcher(scope,
    new ObjectQuery("SELECT * FROM LENOVO_GAMEZONE_DATA"));
var obj = s.Get().Cast<ManagementObject>().First();

var p = obj.GetMethodParameters("SetSmartFanMode");
p["Data"] = (uint)255;
obj.InvokeMethod("SetSmartFanMode", p, null);
```

Surface to implement, with the return-property names that actually apply:

| Purpose | Class | Method | Out property |
|---|---|---|---|
| CPU temp | `LENOVO_FAN_METHOD` | `Fan_GetCurrentSensorTemperature` (`SensorID`=3) | `.CurrentSensorTemperature` |
| GPU temp | `LENOVO_FAN_METHOD` | `Fan_GetCurrentSensorTemperature` (`SensorID`=4) | `.CurrentSensorTemperature` |
| Fan RPM | `LENOVO_FAN_METHOD` | `Fan_GetCurrentFanSpeed` (`FanID`=0,1) | `.CurrentFanSpeed` |
| Full-speed flag | `LENOVO_FAN_METHOD` | `Fan_Get_FullSpeed` | `.Status` |
| Pin fans | `LENOVO_FAN_METHOD` | `Fan_Set_FullSpeed` (`Status`) | — |
| Mode read | `LENOVO_GAMEZONE_DATA` | `GetSmartFanMode` | `.Data` |
| Effective mode | `LENOVO_GAMEZONE_DATA` | `GetThermalMode` | `.Data` |
| Mode write | `LENOVO_GAMEZONE_DATA` | `SetSmartFanMode` (`Data`) | — |
| Max RPM | `LENOVO_FAN_TABLE_DATA` (property, not method) | `CurrentFanMaxSpeed` = 4300 | — |

**Firmware quirks to encode, all measured on HACN46WW:**

- `.Data` is wrong for the two fan-method reads — they use `.CurrentFanSpeed` / `.CurrentSensorTemperature`. This cost time in the original investigation; bake it into the port.
- `GetCPUTemp` / `GetGPUTemp` return 0. Do not use them.
- Only `SensorID` 3 and 4, and only `FanID` 0 and 1, are valid. Everything else throws "Invalid object".
- `Fan_Get_MaxSpeed` returns null out-params. Read `LENOVO_FAN_TABLE_DATA.CurrentFanMaxSpeed` instead.
- **Custom Mode oracle is `GetSmartFanMode == 255`, not `GetThermalMode`** — `ThermalMode` stays at 3 in Custom Mode. Getting this wrong makes a working run report failure.
- `Set_Custom_Mode_Status(1)` does nothing on this BIOS. Only `SetSmartFanMode(255)` engages Custom Mode.
- `Fan_Set_Table` is unusable (`FanTable_Len = 0` everywhere, even in Custom Mode). No hardware fan curve — hence the software one below.
- Custom Mode is AC-gated. Refuse or warn when on battery (`Win32_Battery.BatteryStatus == 2` means AC).

Reads must be individually try/caught and degrade to "n/a" — a single throwing sensor must never kill the poll loop.

---

## Elevation and auto-start

You chose **UAC prompt every launch** (`app.manifest` with `requireAdministrator`). One constraint that forces part of the design:

> Windows **silently refuses** to launch elevated apps from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The entry is skipped with no error and no prompt. Task Scheduler is the only mechanism that can start an elevated app at logon at all.

So auto-start uses a scheduled task either way, and the privilege level becomes the switch:

- **Default (your choice):** task registered with `RunLevel = LUA` (normal). At logon the task starts the exe, the manifest triggers UAC, you approve once per boot.
- **Optional checkbox — "Start without UAC prompt":** re-registers the same task with `RunLevel = Highest`. Silent elevated start, no prompt. This is what Legion Toolkit and most tray tools do. Off by default; there if the per-boot prompt gets old.

Implementation: `AutoStart.cs` shells `schtasks.exe /Create /TN "LegionFanTray" /SC ONLOGON /RU <user> [/RL HIGHEST] /TR "<exe path>" /F`. Avoids a `TaskScheduler` NuGet dependency. Toggle lives in the tray context menu and the dashboard.

Single-instance guard via a named `Mutex` in `Program.cs` — two copies both writing fan state would fight.

---

## Tray icon

`NotifyIcon` with:

- **Tooltip** (hover): `CPU 77°C · GPU 52°C · 2800/2700 rpm · Balanced`
- **Double-click**: show/focus the dashboard
- **Single-click**: nothing (avoids fighting the double-click)
- **Right-click context menu**:
  - `Fans: MAX` (the `custom max` path) — checkable, reflects live state
  - `Fans: Auto (Balanced)` — the `custom off` path
  - separator
  - `Mode ▸` Quiet / Balanced / Performance
  - `Auto-max curve` — checkable toggle
  - separator
  - `Suspend Vantage during writes` — checkable, **off by default** (proven unnecessary on this machine; kept as a diagnostic)
  - `Start with Windows` — checkable
  - `Open dashboard`
  - `Exit`

**Optional:** render the icon dynamically as the CPU temp number, so the tray shows a live figure without hovering. ~20 lines of GDI+ into a `Bitmap` → `Icon.FromHandle`. Must dispose the old handle each update or it leaks GDI objects.

**Windows 11 note:** tray icons go to the hidden overflow flyout by default. To get it literally next to the clock as you described, the user drags it out of the overflow once — the app can't do that itself. Worth a one-time first-run balloon tip explaining it.

---

## Dashboard window

Frameless-ish small window, opens near the tray, `ShowInTaskbar = false`, closing hides rather than exits.

You asked for gauge **or** big numbers as an option — so both, with a view toggle persisted in settings:

- **Gauge view** — two GDI+ arc gauges (CPU, GPU) with colour bands (green < 65, amber 65–85, red > 85), plus two fan RPM readouts drawn as horizontal bars against the 4300 ceiling.
- **Numbers view** — large plain figures, tighter window, faster to read.

Shared below either view: current mode line (`Custom (fans pinned)` / `Balanced` / …), AC-vs-battery indicator, and the action buttons:

`FANS MAX` · `RELEASE` · `Quiet` · `Balanced` · `Performance` · `Auto-max: on/off`

Buttons disable themselves while a write is in flight, and re-read state afterward — same honest-verdict discipline as the script: never assume a write worked, always read back.

Poll timer: 1 s while the dashboard is visible, 5 s when only the tray is live (keeps idle WMI traffic down — this is the polling that got Vantage disabled in the first place, years ago).

---

## Safety

You selected **revert on exit/crash**. Layered, because a tray app gets killed in a lot of different ways:

1. `Form.FormClosing` / `Application.ApplicationExit` — normal exit.
2. `AppDomain.CurrentDomain.ProcessExit`.
3. `AppDomain.CurrentDomain.UnhandledException` + `Application.ThreadException` — release fans, then rethrow/log.
4. `SystemEvents.SessionEnding` — Windows shutdown or logoff.
5. **Dirty-flag file** — write `%LOCALAPPDATA%\LegionFanTray\custom-mode.lock` the moment Custom Mode is engaged; delete it on clean release. On startup, if the file exists, immediately restore Balanced and clear it. This is the only thing that covers Task Manager "End task" and hard power events, where no handler runs at all.

Release sequence is the script's `Exit-CustomMode` order: `Fan_Set_FullSpeed(false)` → `SetSmartFanMode(2)` → read back and confirm.

**Known gap, not being built** (you didn't select it): no sleep/resume handling. Resuming from sleep may leave the EC in an inconsistent state, and the app won't re-assert. The dirty-flag check only runs at process start, not at resume. If this bites in practice, `SystemEvents.PowerModeChanged` is the hook — a later addition, not this pass.

---

## Auto-max curve (the software fan curve)

The interesting feature: the firmware refuses `Fan_Set_Table`, so the app implements a two-point curve in software.

State machine, evaluated on each poll:

- **Enter boost** when CPU ≥ `HighThreshold` (default **85 °C**) for `EnterSamples` consecutive polls (default **3**, ≈ 6 s at 2 s polling). The sample count keeps a momentary spike from triggering it.
- **Exit boost** when CPU ≤ `LowThreshold` (default **70 °C**) for `ExitSamples` consecutive polls (default **5**, ≈ 10 s). The 15 °C gap plus the longer exit window is the hysteresis — without it the fans oscillate on and off at the threshold, which is worse than either state.
- **Only on AC.** On battery, disable the curve entirely and don't attempt Custom Mode.
- **Ownership tracking** — record whether the current fan state was set by `Manual` or `Auto`. Auto must never release fans a human pinned. If the user clicks `FANS MAX`, ownership becomes Manual and the curve stops managing state until they click `RELEASE`.
- **Off by default.** Opt-in via the tray menu; thresholds editable in the dashboard.

Everything is settings-driven, nothing hard-coded, so the thresholds can be tuned against real behaviour.

---

## Settings

JSON at `%LOCALAPPDATA%\LegionFanTray\settings.json`. Fields: `ViewMode` (Gauge|Numbers), `AutoMaxEnabled`, `HighThresholdC`, `LowThresholdC`, `EnterSamples`, `ExitSamples`, `PollIntervalMs`, `SuspendVantage`, `AutoStart`, `AutoStartSilent`, `ShowTempInTrayIcon`. Missing file → defaults; malformed file → defaults plus a balloon warning, never a crash on startup.

---

## Build

```
dotnet publish app/LegionFanTray.csproj -c Release -r win-x64 ^
  --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Targets `net8.0-windows`, `<UseWindowsForms>true</UseWindowsForms>`, one `System.Management` PackageReference. Output is a single .exe. Works on Windows 10 (this machine) and 11 unchanged.

---

## Verification

1. **WMI parity** — run `.\Legion-FanControl.ps1 probe` and the app side by side; temps, RPM and mode must agree. The script is the oracle here since it's confirmed correct against hardware.
2. **Fans actually move** — click `FANS MAX`, confirm 2800 → 4300 rpm in the dashboard *and* independently via `.\Legion-FanControl.ps1 custom status`. Then `RELEASE` and confirm return to Balanced.
3. **Crash safety** — engage max, kill the process from Task Manager, confirm the lock file exists, restart the app, confirm it restores Balanced on its own.
4. **Auto-start** — register it, reboot, confirm the app comes up (and, with the default setting, that the UAC prompt appears). Then flip the silent checkbox and reboot again to confirm no prompt.
5. **Auto-max curve** — set `HighThreshold` to something reachable (e.g. 60 °C), run a CPU load, watch it engage after the sample delay and release after the temperature drops past the low threshold. Confirm no oscillation at the boundary.
6. **Battery** — unplug, confirm the curve disables itself and Custom Mode actions warn instead of failing silently.
7. **Single instance** — launch twice, confirm the second exits.

**Don't run `.\Legion-FanControl.ps1 custom max` from the CLI while the app's auto-max is active** — two writers on the same firmware will fight. Reads from both are fine.

---

## Open question for later

`LENOVO_CPU_METHOD.CPU_Set_LongTerm_PowerLimit` / `CPU_Set_ShortTerm_PowerLimit` and the GPU equivalents are still unexplored — the vendor's own PL1/PL2 interface, strictly better than RyzenAdj. If 4300 rpm plus a software curve still isn't enough thermal headroom, that's the next lever, and it would slot into this app as another tray control. Out of scope for this pass.
