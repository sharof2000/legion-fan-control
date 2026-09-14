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

## Notes

- AMD controls are available only when a compatible AMD adapter is detected; they are global
  driver settings and take effect when the affected app next starts.
- NVIDIA's Battery Boost, Max Frame Rate, and Whisper Mode remain managed in the NVIDIA app,
  because its profile database has no supported write API.
- Existing fan-control behavior is unchanged.
