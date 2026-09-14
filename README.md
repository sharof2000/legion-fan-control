# Legion Fan Control

[![CI](https://github.com/sharof2000/legion-fan-control/actions/workflows/ci.yml/badge.svg)](https://github.com/sharof2000/legion-fan-control/actions/workflows/ci.yml)

A tray app and a PowerShell tool for forcing the fans on a Lenovo Legion laptop to full
speed, with a live readout of temperatures and fan RPM.

![The Legion Fan Tray dashboard: CPU and GPU temperature gauges, both fan RPM bars, the
mode buttons, and the auto-max and CPU cap controls.](docs/screenshot.png)

> ### Read this first: hardware scope
>
> Everything here was developed and confirmed on **one machine** — a Lenovo Legion
> S7-15ACH6 (model 82K8), BIOS **HACN46WW**, Ryzen 7 5800H, Windows 10 19045.
>
> Other Legion models may work, because the app talks to Lenovo's own WMI provider rather
> than poking the embedded controller directly. **Non-Lenovo hardware will not work at
> all** — the WMI classes it calls simply do not exist there.
>
> The sensor IDs, the fan indices and the 4300 rpm ceiling are values measured on that
> laptop. If yours differs, the readings will be wrong before anything else is. See
> [Probing your own machine](#probing-your-own-machine).

## How this came about

My laptop runs hot, and the fans never seemed to work as hard as I wanted them to. The stock
Quiet / Balanced / Performance modes hold both fans around 2300 rpm at 61 °C, and even at
77 °C the controller only gives you about 2800. The hardware will do 4300, and Lenovo's own
software gives you no way to ask for it.

So I went looking for something that already solved this. That turned out to be a dead end.
The generic fan utilities want direct embedded-controller access and have no profile for this
model. The Legion-specific tools are mostly written for the newer, better-known generations —
they either did not detect this laptop, or they detected it and then quietly did nothing. I
could not find a single working answer for the S7-15ACH6 specifically, and plenty of forum
threads where somebody asked the same question and never got a reply.

At that point the choice was to give up or to find out what the firmware would actually accept.
I picked the second one, and it went in three stages:

**1. Probe the hardware.** Lenovo ships its own ACPI-WMI classes, so the information is
technically right there — it is just undocumented. I wrote a PowerShell script to enumerate
every Lenovo WMI class on the machine and dump what each method returns. That is the `probe`
subcommand in [`scripts/Legion-FanControl.ps1`](scripts/Legion-FanControl.ps1), and it is still
the first thing to run on an unfamiliar machine.

**2. Work out what actually moves the fans.** This was the slow part, and it involved a lot of
writes that reported success and changed nothing. The findings are written up properly in
[`docs/findings.md`](docs/findings.md) — including the things that turned out to be false leads,
which felt worth recording so the next person does not repeat them. The summary is below.

**3. Wrap it in something usable.** The script works, but it needs an elevated PowerShell window
every single time, and it gives you no at-a-glance view of what the temperatures are doing. So
the working path became a small tray app: it sits next to the clock, shows the CPU temperature
on the icon itself, and puts the same actions two clicks away. It also does the one thing the
CLI cannot — since the firmware refuses to store a fan curve, the app polls temperature and
toggles full speed itself.

The script did not get retired when the app arrived. It is still the better tool for probing,
and it is what I use to check the app is telling the truth.

## What the firmware actually does

The short version: on this BIOS the obvious way to control the fans does not work, and the way
that does work is not documented anywhere.

- The three normal thermal modes (Quiet, Balanced, Performance) are **EC-owned curves**.
  Performance raises the curve, but it still only holds ~2800-3800 rpm until temperature
  demands more, and it actively *drops* any `Fan_Set_FullSpeed` you write. That is why every
  early attempt looked like a firmware refusal.
- Fan forcing is gated behind **Custom Mode**, and the only thing that engages Custom Mode is
  `SetSmartFanMode(255)`. The documented-looking `Set_Custom_Mode_Status(1)` returns success
  and does nothing at all.
- `ThermalMode` does not follow into Custom Mode, so it is useless for telling whether Custom
  is engaged. `SmartFanMode` is the only reliable oracle.
- **There is no hardware fan curve to program.** `Fan_Set_Table` is unusable here —
  `FanTable_Len` stays 0 on every one of the 18 FanID × SensorID pairs, even inside Custom
  Mode. Full-speed on and off is the entire vocabulary.

So the working sequence is `SetSmartFanMode(255)` followed by `Fan_Set_FullSpeed(true)`, and
because the firmware will not hold a curve, the app polls the temperature and toggles
full-speed itself.

The full investigation, including what was measured and what was ruled out, is in
[`docs/findings.md`](docs/findings.md). The reasoning behind the app's design — why a tray app,
why a software curve, what the safety model has to cover — is in
[`docs/design.md`](docs/design.md), written before any of the C# existed.

## What's in here

| | |
|---|---|
| `src/` | **Legion Fan Tray** — the tray app. C# / .NET 8 WinForms. |
| `scripts/Legion-FanControl.ps1` | The CLI the app was ported from, and the probe tool. |
| `docs/` | The hardware research and the app design document. |

## Download

Grab a zip from the
[Releases page](https://github.com/sharof2000/legion-fan-control/releases/latest). No build
needed.

| | |
|---|---|
| `...-selfcontained.zip` | ~62 MB. Carries the .NET runtime inside it. **Pick this one** unless you know you already have the runtime. |
| `...-framework.zip` | ~550 KB. Needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed. |

`SHA256SUMS.txt` ships alongside them if you want to check what you downloaded.

**Windows will warn you when you run it.** The exe is unsigned, so SmartScreen shows
*"Windows protected your PC"* the first time — click **More info → Run anyway**. That is not
something I can fix without buying a code-signing certificate, and I would rather write it
down here than have it be a nasty surprise on a tool that also asks for administrator rights.

## Build

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Then:

```bat
build.bat              :: one self-contained .exe, no .NET needed on the target
build.bat framework    :: small .exe, needs the .NET 8 Desktop Runtime installed
build.bat clean        :: wipe dist\, bin\ and obj\
```

The result is `dist\LegionFanTray.exe`.

The self-contained build lands at about 62 MB, because it carries the entire WinForms
runtime inside it. If you already have the .NET 8 Desktop Runtime installed and would rather
have a ~550 KB file, use `build.bat framework`.

`build.bat` kills a running `LegionFanTray.exe` before it starts, because otherwise the build
fails with a file-lock error that tells you nothing useful.

The version number comes from [`appVersion.txt`](appVersion.txt) in the repo root, read by
`Directory.Build.props`. That is the one place to edit it; the build and the CI workflows pick
it up on their own.

Every push is built on a GitHub `windows-latest` runner
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). Worth being clear about what that
badge at the top actually means: it says the project compiles and publishes on a clean
machine, and nothing more. A GitHub runner has no Lenovo hardware, so none of the WMI classes
this app drives even exist there. Every functional claim on this page was checked by hand on
the one laptop named at the top.

## Running it

Double-click the exe. **It must run as administrator** — this is not optional and not
paranoia: the Lenovo WMI classes return `Access denied` for *reads* as well as writes to a
non-elevated process, so without it every reading shows `n/a`. The manifest requests
elevation, so you get a UAC prompt rather than a mystery.

- **Double-click the tray icon** for the dashboard: live CPU and GPU temperatures, both fan
  speeds, and the buttons.
- **Right-click** for the quick actions: `Fans: MAX`, release, mode switching, the auto-max
  toggle, GPU / Power profiles and switches, and auto-start.
- The tray icon itself draws the current CPU temperature, so you can leave the window closed.
  Turn that off in the menu if you would rather have a plain icon.
- On Windows 11 the icon starts life hidden in the overflow flyout. Drag it out once and it
  stays next to the clock.

Custom Mode and Performance are AC-gated by the firmware, so fan forcing does nothing on
battery. That is the laptop's rule, not the app's.

## Safety

This is the part worth reading twice.

**In Custom Mode the host owns the fans, and nothing brings them back down on its own.** If
the app engages full speed and then dies, the fans stay at 4300 rpm until something tells
them otherwise.

So the app releases the fans on normal exit, on process exit, on an unhandled exception, and
on Windows shutdown. For the cases where none of those handlers run at all — Task Manager's
"End task", a hard power loss — it writes a lock file at
`%LOCALAPPDATA%\LegionFanTray\custom-mode.lock` the moment Custom Mode is engaged, and deletes
it on a clean release. Finding that file at startup means a previous run died dirty, and the
app restores Balanced before doing anything else.

**Known gap:** there is no sleep/resume re-assert. Resuming from sleep can leave the EC in an
inconsistent state, and the lock-file check only runs at process start, so it will not catch
that. If you suspend the machine a lot, keep an eye on it.

## Auto-max curve

Since the firmware refuses to hold a fan table, the app polls the CPU temperature and toggles
full speed itself.

There is **one** number to set: the engage point. The release point is always 10 °C below it,
shown next to the dropdown, and is not separately editable — a `LowThresholdC` hand-edited
into `settings.json` gets recomputed from the engage point at startup.

That gap is not an arbitrary default. With a single threshold for both directions the fans
would max out at 80 °C, cool to 79, release, climb straight back to 80, and max again — a full
cycle every few seconds, which is audibly worse than either state on its own. The 10 °C
deadband, plus requiring more consecutive samples to release (5) than to engage (3), is what
stops that.

It runs on AC only, it is off by default, and it never releases fans that you pinned by hand.

## CPU cap

The `CPU cap` dropdown sets the Windows **max processor state** through `powercfg`
(`SUB_PROCESSOR` / `PROCTHROTTLEMAX`), on both the AC and DC rails of the active scheme.

This is not fan control. It is heat control at the source, which means the fans have less work
to do — the two go together well.

- **99%** disables Turbo, the main thermal driver on the 5800H. By far the most useful setting
  here.
- **~80%** is roughly the floor of diminishing returns.
- **100%** removes the cap.

The value applies immediately and is verified by reading it back, because `powercfg` exits 0
whether or not it actually changed anything.

One trap worth knowing: **the cap is stored per power scheme**, so switching power plans
silently drops it. The dashboard re-reads the live value every time you open it, so what you
see is what the machine actually has.

## GPU and power profiles

The dashboard's **GPU / Power** tab groups the performance and battery-life controls that sit
alongside fan control. The same profile choices and the most-used switches are available from
the tray icon's right-click menu.

Three profiles apply a coherent set of Windows power-plan values, AMD options (when an AMD
adapter is present), and the app's per-application GPU list:

| Profile | Intended result |
|---|---|
| `Full power` | Keeps CPU performance available, prevents Battery Saver from enabling itself, keeps the PCIe link awake on battery, removes frame caps, and sends listed apps to the NVIDIA GPU. |
| `Balanced` | Restores the vendor-style defaults: normal CPU management, moderate PCIe power saving, no frame cap, and no app GPU overrides. |
| `Power save` | Caps CPU to 80% on battery, uses stronger PCIe power saving, enables a 30 FPS cap and AMD power-saving features, and sends listed apps to the Radeon iGPU. |

Before applying the first profile, the app captures the relevant values. **Restore original**
puts that snapshot back; it is the state from before this app first changed these controls, not
the state before the most recent profile. Individual switches are also reversible: they save
their previous power-plan value before changing it and restore that value when turned off.

The per-app GPU list is the same Windows graphics preference used by **Settings → System →
Display → Graphics**. Add an executable and choose either the power-saving Radeon iGPU or the
high-performance NVIDIA dGPU; remove its override to return to *Let Windows decide*. These
preferences are per Windows account. If UAC is answered using a different administrator
account, the override is written for that account instead.

On AMD systems, the tab can read and change global Radeon driver options: Frame Rate Target
Control (FRTC), Radeon Chill, Radeon Boost, Vari-Bright, and AMD Software's Power Saver
auto-enable setting. Changes apply when the affected application next starts, and opening AMD
Software can rewrite them, so the app reads them back on refresh. Per-app AMD profiles are not
supported because their database is undocumented.

NVIDIA Battery Boost, Max Frame Rate, and Whisper Mode are intentionally not changed by this
app. They live in NVIDIA's undocumented binary profile database; use the NVIDIA app instead.
When on battery, the dashboard warns that Battery Boost may be capping the dGPU (commonly at
30 FPS) and provides a shortcut to the vendor app.

## The PowerShell tool

The subcommands, in full:

| | |
|---|---|
| `monitor` | Live read-only dashboard: CPU/GPU temp, both fan RPMs, current mode. |
| `probe` | Read-only capability dump of every Lenovo WMI class. Start here on a new machine. |
| `custom max` | Engage Custom Mode and pin both fans at 4300 rpm. |
| `custom off` | Release, back to Balanced. Also `custom status`, `custom on`, `custom fullspeed <on\|off>`. |
| `mode <q\|b\|p>` | Set Quiet / Balanced / Performance, verified by reading `ThermalMode` back. |
| `force` | Attempt `Fan_Set_FullSpeed` on its own, then verify by RPM readback. |
| `cap <percent>` | Cap CPU max processor state via `powercfg`. `uncap` restores 100%. |
| `help` | Usage. The default when you give it nothing. |

```powershell
.\scripts\Legion-FanControl.ps1 monitor     # watch it live
.\scripts\Legion-FanControl.ps1 probe       # what does my firmware expose?
.\scripts\Legion-FanControl.ps1 custom max  # fans to 4300 rpm
.\scripts\Legion-FanControl.ps1 custom off  # release, back to Balanced
```

Everything except `help` needs an elevated terminal, for the same read-access reason as the
app. Every write path reads the value back and reports an honest OK / PARTIAL / FAIL — a bare
`ReturnValue = 0` is never taken as success.

### Don't run both as writers

**Do not run `custom max` from the script while the app's auto-max is active.** Two writers on
the same firmware will fight each other, and diagnosing that is no fun.

Reading from both at once is completely fine, and is the intended way to verify the app:

```powershell
.\scripts\Legion-FanControl.ps1 custom status
```

## Probing your own machine

If you have a different Legion, start with the script, not the app:

```powershell
.\scripts\Legion-FanControl.ps1 probe
```

That dumps every Lenovo WMI class it can reach and shows what your firmware actually exposes.
Compare it against [`docs/findings.md`](docs/findings.md).

Things very likely to differ on your hardware:

- **Sensor IDs.** This machine answers on 3 (CPU) and 4 (GPU); everything else throws
  `Invalid object`. The dedicated `GetCPUTemp` / `GetGPUTemp` methods return 0 on this BIOS
  and are deliberately unused.
- **The fan ceiling.** 4300 rpm is a measured value, read from
  `LENOVO_FAN_TABLE_DATA.CurrentFanMaxSpeed` because `Fan_Get_MaxSpeed` returns null
  out-parameters.
- **Out-parameter names.** They are BIOS-quirky. Values arrive in
  `.CurrentSensorTemperature` and `.CurrentFanSpeed`, not the `.Data` you would expect.
- **Whether `Fan_Set_Table` works.** If it does on your BIOS, you can have a real hardware
  curve and you do not need the software one at all.

## Settings

`%LOCALAPPDATA%\LegionFanTray\settings.json`. A malformed file falls back to defaults with a
balloon warning rather than refusing to start, so a bad edit cannot lock you out.

Crash details, if there are any, land next to it in `crash.log`.

## License

MIT — see [LICENSE](LICENSE).

This drives laptop cooling hardware through an undocumented firmware interface, and it comes
with no warranty. Read the [Safety](#safety) section before leaving it running unattended.
