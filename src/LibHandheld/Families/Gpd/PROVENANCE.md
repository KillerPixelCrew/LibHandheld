# GPD native implementation

The model and protocol facts were independently reimplemented. No HC or HHD implementation code is
included.

Primary source: HandheldCompanion public source at `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`,
`HandheldCompanion/Devices/GPD`, `Devices/IDevice.cs`, and `Resources/Devices/GPD*.json`. Secondary
source: the pinned HHD inventory source, `src/hhd/device/gpd/win/wincontrols.py`, `wincontrols2.py`,
and `const.py`.

- Twenty concrete HC model selectors cover WIN 2, WIN 3, six WIN 4 CPU variants, four WIN Mini
  variants, seven WIN Max 2 variants and WIN 5. The abstract WIN Max 2 class supplies inherited
  facts rather than another hardware model. Exact SMBIOS product and CPU brand selectors distinguish
  reused board IDs; unknown processors do not inherit an arbitrary EC map.
- Original WIN 4 uses the 0x2E/0x2F SuperIO window. Chip signature 0x55 at 0x2000 gates the
  documented unlock bit at 0x1060. Its fan command is 0xC311, scale 0–127.
- Later WIN 4 and WIN Max 2 use 0x4E/0x4F, manual flag 0x275 and duty 0x1809, scale 0–184. WIN Mini
  uses 0x47A, scale 0–244. Mini's known broken speed readback is not published as measured RPM.
- WIN 5 drives both 0x47A and 0x47B, scale 0–244; RPM is high byte 0x478 and low byte 0x479.
  Firmware rear-button reports have genuine edges on 2F24:0137/0135, usage FF00:0001. Firmware
  without that interface retains the HC keyboard fallback.
- WinControls V1/V2 configuration formats retain firmware layout checks, page offsets and pre-EEPROM
  checksums. Rumble intensity, stick deadzones/boundaries and supported RGB fields are persistent
  explicit user commands. V1 rear acquisition changes only macro keys/delays and restores only those
  bytes, retaining lighting and stick preferences changed during the cycle.
- The 2025 Mini's 045E:002D controller configuration endpoint uses the V2 format. Its rear macro
  regions are captured and restored independently of persistent fields. GPD digitizers 093A:0255 and
  0911:5288 use their actual Windows HID contact and axis descriptors; they supply canonical touch
  coordinates and click state rather than synthesizing mouse movement.
- GPD's cloned Xbox VID/PID is joined to the GPD companion collection on the same built-in USB hub
  before a modern model claims its XInput slot. Ambiguous identical pads are refused. Legacy WIN
  2/WIN 3 keep HC's exact model plus VID/PID path; hardware identity acceptance for those older
  controller layouts remains a separate live check.

Power and EC handles open only after exact model selection. Each service degrades independently; CPU
or configuration failure does not stop the physical controller. Native operations share the existing
lifecycle serializer, first-original journals, suspend/resume handling and controller ownership
transitions. Written observations are distinct from verified readback; uncertain writes are not
retried.

This is source-backed implementation evidence. Compilation, source regression tests, and live device
acceptance are separate checks; no GPD hardware was probed while implementing this lane.
