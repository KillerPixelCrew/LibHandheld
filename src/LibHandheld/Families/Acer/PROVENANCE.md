# Acer Predator Atlas 8

## Initial physical input contribution

Source: [InputPlumber issue 759](https://github.com/ShadowBlip/InputPlumber/issues/759), reviewed
2026-10-27. This is an independently written Windows implementation of reported hardware facts, not
a port of InputPlumber application code or its temporary Linux configuration.

- [Inventory](https://github.com/ShadowBlip/InputPlumber/issues/759#issuecomment-6310419266): Acer
  system `Predator PA08-I51`, PTL board `F900_PTG`, BIOS V1.03, EC 1.2. Detection requires both
  system and board identities. Neither the marketing name nor USB identity alone selects it.
- [HID captures](https://github.com/ShadowBlip/InputPlumber/issues/759#issuecomment-6341859840): USB
  `0502:F901`, release `0004`; interface 01 contains top-level `FFF1:F5`, 16-byte reports. `9A` byte
  1 masks `02` / `04` represent M1 / M2; zero releases them. `9B` reports must not alter those
  edges. `9E 00 00 05 00` is a Guide notification without a release. The library synthesizes a 200
  ms tap using its existing OEM hold duration, releasing it on stop/disconnect.
- Interface 02 top-level `0088:0001` has three-byte QAM reports `37 81 FF` / `37 00 00`. This is a
  firmware tap delivered after the physical button is released, not a physical hold measurement. The
  reported F22 long-hold workaround is not registered as a second Guide source.
- Both OEM collections must be unique and share the same composite USB location. The regular pad and
  rumble use the existing exact-VID/PID XInput service. No calibration, mode switch, firmware
  profile write or speculative controller command is sent on acquisition.

The reporter found that consuming vendor input prevents Linux controller freezes. This is evidence
for retaining the raw reader, not proof of the same behavior or a required workaround on Windows.
Rear buttons may depend on the previously selected firmware profile; the library does not rewrite
it.

## Boundaries and acceptance

The initial contribution supplied exact detection, standard controller/rumble routing, OEM button
decoding and stock gamepad glyphs. The on-device implementation below adds EC power, fan control and
RPM, battery protection, Windows motion and volatile controller lighting. Optional Intel processor
settings remain owned by the shared processor service and declare its IntelKx dependency.

Predator Sense 1.0.1563.0 was supplied separately by the maintainer for offline protocol research.
The follow-up [power and fan protocol research](POWER_FAN_PROTOCOL.md) records the separate EC HID
power path, WMI fan commands, field layouts and remaining recovery/acceptance work. Its proprietary
binaries and decompiled source are not included in the library. Protocol findings are research
inputs; motion units/orientation, native controls and restoration need their own reviewed
implementation and measured acceptance.

## PA08-I51 on-device implementation, 2026-10-27

The maintainer authorized autonomous hardware work. The unit reports system Acer / Predator
PA08-I51, board PTL / F900_PTG and BIOS V1.03. Its controller is 0502:F901 release 0100, with a
15-byte FFF1:00F5 input collection and three-byte 0088:0001 QAM collection. The original published
0004 / 16-byte variant remains supported. Both are exact report shapes; other releases are refused.

Implemented controls and evidence:

| Area     | Mechanism and observed result                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Power    | I2C HID 1025:174B, FF05:0001, 65-byte feature reports, ID A0; EC data version 0800. PL1 limits are 10..40 W; PL2 maximum is 65 W. Both 20/40 W and unified 20/20 W Manual commands read back correctly. Release restored the original 25/45 W manual profile and active Turbo mode. Uninterpreted profile fields were retained.                                                                                                                                                                                                                                                             |
| Fans     | Health mask 02CD discovers SYS and SYS2. AcerGamingFunction WMI supplies Auto, Max and fixed-speed commands. EC health indices 4 and 8 supply the two RPM readings; index 1 supplies CPU temperature (53 degrees C in the final lifecycle trial). A bounded Max trial increased readings from 2829/2807 to 3281/3237 RPM. Release sent Auto. Behavior getters returned zero, so original behavior was unknown and restoration was reported unverified.                                                                                                                                      |
| Battery  | BatteryControl.GetBatteryHealthControlStatus and SetBatteryHealthControl, battery 1, function mask 1. Optimized charging toggled from disabled to enabled, read back enabled, then restored disabled with a clear journal. Calibration and smart-charge schedules are not invoked. No arbitrary percentage ceiling or bypass is claimed.                                                                                                                                                                                                                                                    |
| Motion   | Windows exposes both gyrometer and accelerometer through WinRT and the legacy Sensor API. Fresh samples reached canonical controller frames. The standard landscape Windows frame maps to application (X, Z, -Y); physical positive-axis trials remain outstanding.                                                                                                                                                                                                                                                                                                                         |
| Lighting | Controller FFA0:0001, 64-byte reports. Output ID 0F requests profile 0; input ID 10 returns RAM profile 1. Only the 883-byte lighting section at offset 586 is addressable. Brightness, speed, solid/breathing effects and a shared nine-LED color use RAM command 21. Brightness 50 -> 45, both effects, color 123456 and speed 10 read back correctly; release restored every lighting byte. The dedicated command input queue is flushed before snapshots to discard stale replies. No mapping or ROM command is sent. Lighting acquisition currently admits measured release 0100 only. |
| Lifetime | Physical controller and motion samples, four OEM descriptors, controller disable/re-enable, library suspend/resume and clean stop were observed. A bounded 10% rumble command was followed by an explicit zero; no human perception is claimed.                                                                                                                                                                                                                                                                                                                                             |

Recovery uses captured model/firmware identity; lighting also binds the controller release. Unknown
observations remain unknown, writable controls do not require current-value readback, and uncertain
commands are not retried. Profile edits require retained bytes so unrelated fields cannot be
invented. Fan recovery uses exact original behavior/speed where known; otherwise it returns to
firmware Auto and reports incomplete verification.

The optional KX helper could not load its kernel driver: Windows security returned error 225.
Protection was left enabled. This affects optional processor tuning, independently of the native EC
power path. Missing or blocked helpers may therefore leave the aggregate device state degraded while
native Atlas services remain usable.

Native source identities match the earlier supplied HardwareService and Agent hashes. Battery
protocol inspection used installed AcerCCAgent.exe 1.5.51, SHA-256
`41e0016cfb45b7d3dcbd7568ca118cc498365b452e631e07e0d225293330c530`, with Ghidra 12.2 and ILSpy 11.2.
Protocol implementations are independent code; proprietary binaries and decompilation remain
private. Windows sensor reference axes follow
[Microsoft's sensor orientation contract](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/sensor-orientation).

HardwareVerified remains false. Physical Guide/QAM/M1/M2 attribution on release 0100, positive-axis
motion, perceptual haptics, held-button disconnect, actual Windows sleep/wake and visible Steam
acceptance were not established by autonomous software trials. Library suspend/resume is not an OS
sleep test. Builds, fixtures and Overlay replays do not replace those observations.
