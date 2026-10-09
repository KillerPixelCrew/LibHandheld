# ZOTAC Gaming Zone

Windows facts come from HandheldCompanion commit `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`:

- `Devices/IDevice.cs`: exact `PC PARTNER LIMITED` / `ZOTAC` baseboard vendor and `G0A1W` board
  selector.
- `Devices/Zotac/GamingZone.cs`: 8–30 W envelope; indexed EC `4E/4F`, fan mode `044A`, duty `044B`
  scaled to 255; vendor HID `1EE9:1590`, 65-byte input/output shape; command header, CRC, rear-key
  remap, lighting, saved-profile reload; `UMAInterface.SetEcValue(Index=122)` allocation formula.
- `Controllers/Zotac/ZoneController.cs`: independent interface 01 four-byte wheel input. Buffer byte
  2 without report ID corresponds to Windows report byte 3.
- `Resources/Devices/GamingZone.json`: separate acceleration and angular velocity coordinate
  matrices.

HHD commit `a87fb308f9f14d4a78ef3cb0b8515827459f8466`, `src/hhd/device/generic/const.py` and
`base.py`, adds the exact system-product alias `ZOTAC GAMING ZONE` and cross-checks the USB
identity. Its lack of Zone motion takes no precedence over HC's real Windows sensor implementation.

Gamepad and haptics use the matched physical Windows XInput slot. Wheel edges are separately named
OEM events; they are not falsely mapped to rear paddles. Motion samples come only from actual
Windows sensor streams, never inferred from XInput or wheel reports.

M1/M2 takeover changes volatile mappings and records an intent to reload the saved firmware profile.
No original mapping read is known. A profile reload is therefore reported unverified, never as
byte-for-byte restoration. Pending crash restoration is attempted once; unconfirmed or failed
restoration is not automatically retried. Lighting persistence first reloads the saved profile so
temporary rear mappings cannot be saved as permanent configuration.

UMA allocation is firmware behavior, distinct from GPU-driver tuning in LibGPUDriverInteract. It is
global and takes effect after Windows restarts. The WMI provider must actually be present; absent
providers produce unavailable controls. No native writes occur during model detection.

These are source-based implementations. No new Zone hardware acceptance has been performed.
