# Changeset: GPU and power controls

**Release type:** minor
**Tag:** 0.1.1

## Added

- GPU / Power dashboard and tray-menu controls with Full power, Balanced, and Power save
  profiles.
- A one-time original-state snapshot and Restore original action for profile changes.
- Reversible Windows power-plan switches for CPU rail locking, Battery Saver auto-enable, and
  PCIe link power on battery.
- Per-application Windows GPU preferences for selecting the Radeon iGPU or NVIDIA dGPU.
- AMD Radeon controls for FRTC, Chill, Boost, Vari-Bright, and Power Saver auto-enable.
- NVIDIA Battery Boost guidance and a shortcut to the installed NVIDIA control application.

## Fixed

- Auto-max now works on a low-wattage charger. AC is detected from the charger line status
  rather than the battery state, so a charger that cannot keep up is still treated as AC.
- An auto-max engagement is always released below the release point, even if the power
  reading flickers or the charger is pulled, instead of leaving the fans pinned.
- Turning auto-max off while it holds the fans now releases them.
- A failed auto-max engage waits 60 s before retrying.
- Releasing the fans returns to the mode that was active before (app and `custom off`). A
  low-wattage charger locks the firmware to Quiet, which is no longer reported as a failure.
- The script's AC check uses the charger line status, like the app.

## Script

- `test` subcommand for measured experiments: `power`, `methods`, `modes`, `custom`, `pulse`,
  and `table` (blind `Fan_Set_Table` with a flat fan level). Results in `docs/findings.md`.

## Notes

- AMD controls are available only when a compatible AMD adapter is detected; they are global
  driver settings and take effect when the affected app next starts.
- NVIDIA's Battery Boost, Max Frame Rate, and Whisper Mode remain managed in the NVIDIA app,
  because its profile database has no supported write API.
