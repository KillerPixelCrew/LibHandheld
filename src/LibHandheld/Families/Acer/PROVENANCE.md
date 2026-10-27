# Acer Predator Atlas 8

[OEM input and software control](OEM_INPUT_AND_DISABLE.md) is the complete operational reference:
physical source/report mapping, rear-mode acquisition/restoration, software disable targets,
post-reboot evidence, persistent package activation blocking and recovery.

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
Rear reports depend on the controller's M-key mode. The runtime now owns the temporary mode through
the controller lifecycle as described in the attended follow-up below; it does not rewrite profile mappings.

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
| Lighting | Controller FFA0:0001, 64-byte reports. Output ID 0F requests profile 0; input ID 10 returns RAM profile 1. Only the 883-byte lighting section at offset 586 is addressable. Brightness, speed, solid/breathing effects and ring colors use RAM command 21. The eight visible segments are mapped below; the ninth wire slot is retained. Brightness 50 -> 45, both effects, color 123456 and speed 10 read back correctly; release restored every lighting byte. The dedicated command input queue is flushed before snapshots to discard stale replies. No mapping or ROM command is sent. Lighting acquisition currently admits measured release 0100 only. |
| Lifetime | Physical controller and motion samples, four OEM descriptors, controller disable/re-enable, library suspend/resume and clean stop were observed. A bounded 10% rumble command was followed by an explicit zero; no human perception is claimed.                                                                                                                                                                                                                                                                                                                                             |

## Attended RGB slot mapping, 2026-10-27

The maintainer observed a retained-profile RAM preview with one static keyframe at 60% brightness.
Its nine RGB slots were red, green, blue, yellow, cyan, magenta, orange, violet and white, in order.
The original lighting section was journaled before dispatch; readback matched the preview bytes.
The preview has a ten-minute limit and restores the original lighting section afterward.

With the screen facing the user, the maintainer identified the left stick ring using the palette,
then confirmed each of the eight segments individually with white while all other slots were dark:

| Physical position | One-based RGB slot | Zero-based RGB index | Observed color |
| --- | --- | --- | --- |
| Top left | 7 | 6 | Orange, FF8000 |
| Top right | 6 | 5 | Magenta, FF00FF |
| Bottom left | 8 | 7 | Violet, 8000FF |
| Bottom right | 5 | 4 | Cyan, 00FFFF |

The right ring was then mapped one white slot at a time:

| Physical position | One-based RGB slot | Zero-based RGB index |
| --- | --- | --- |
| Top left | 2 | 1 |
| Top right | 1 | 0 |
| Bottom left | 3 | 2 |
| Bottom right | 4 | 3 |

Only slot 9 was set to white in the final step. The maintainer reported no visible light and no
additional RGB hardware. This slot remains unassigned; no visible output does not establish its
firmware meaning. Runtime color/effect edits preserve its RGB bytes in the retained keyframes.
The tests restored the complete original lighting section with verified readback and a healthy
recovery journal. No ROM, controller mappings or calibration were changed.

The runtime exposes a shared "All ring segments" color and eight individual `lighting.color`
instances: `left.top-left`, `left.top-right`, `left.bottom-left`, `left.bottom-right`, and the
corresponding `right.*` names. Each individual command changes only its measured RGB triplet in
the active frame. Shared color affects the eight visible slots and retains the ninth. Colors,
brightness, speed and effect selection use the same native backend on both WSGM surfaces.
Effects and brightness remain shared across the rings; independent per-ring timing was not
established. The physical mapping is verified for this controller release, not every firmware.

## Attended button follow-up, 2026-10-27

The maintainer completed the 27-step CLI capture on the same BIOS V1.03 / controller 0100 unit.
The front-left extra button delivered `9E0000050000000000000000000000` three times; these are
Guide taps with synthetic 200 ms release, not measured holds. The front-right extra delivered
`3781FF` / `370000` three times, a roughly 100 ms QAM pulse. Physical long holds were not tested.
Face buttons, D-pad and its up-right diagonal, shoulders, stick clicks, both full-range triggers
and A+B+LB rollover registered. View/Menu came through opposite the step names, and the left
stick's leftward travel reached only -0.133; these need an isolated confirmation before altering
mapping or calibration. The right stick reached both extrema. No reader faults or overflow occurred.

The original rear mode was XInput (1) / Macro (0), which produced no rear reports. Acer's installed
DeviceProxyService uses command 26 hex to read gamepad mode and command 24 hex to select the
gamepad and M-key modes. The response is report 10 / command 27 hex; its bytes 5 and 6 describe
those two modes. Selecting XInput (1) / Combination (1) exposes native rear reports without editing
profile mappings, calibration, lighting, other feature settings or ROM. The runtime journals the
original mode before dispatch, acquires this mode only with controller management, and restores
the original on release, disable or suspend. No user setup in Predator Sense is required.

The maintainer then completed the three-step managed rear capture. Rear LEFT delivered
`9A0400000000000000000000000000`, rear RIGHT `9A0200000000000000000000000000`, each with three
presses and `9A00...` releases. Both together delivered `9A06...`; partial release retained the
other button. Unrelated `9B00000080FF7F0080FF7F00000000` reports did not release held rear buttons.
The existing M1/bit 02 and M2/bit 04 protocol labels remain; measured physical left maps to M2,
right to M1. There were no reader faults or dropped events. Mode acquisition was Owned and release
Idle with verified restoration to the original XInput/Macro mode. This proves rear acquisition
and release on this unit, independently of the normal user-configured profile.

### Front shortcuts and device attribution

The subsequent six-step front capture showed the left short Guide HID report followed about
200 ms later by injected LeftWin+G. Left long presses produced a complete, non-injected
LeftWin-down / Tab-down / LeftWin-up / Tab-up sequence, with no Guide HID notification.
Right holds produced non-injected F22 down/repeat/up; these differ from the shorter QAM HID taps.
The keyboard hook alone cannot identify a physical source or distinguish this clean Win+Tab from
the same keys on another keyboard.

With the maintainer's direction, the Acer user-mode software stack was stopped and its restart
entries disabled for comparison. The maintainer confirmed that Win+G and the Predator Sense
launch disappeared, while Win+Tab persisted. Native EC power, fans, battery, lighting, controller,
motion and OEM services still acquired and stopped cleanly with those applications absent.

The four-step Raw Input capture then attributed both Win+Tab and F22 to the controller's USB
keyboard collection `HID\\VID_0502&PID_F901&MI_02&Col01`, serviced by `kbdhid`, usage 0001:0006.
The volume buttons instead used the separate I2C consumer collection `HID\\1025174B&Col08`,
with reports `33E900` / `33EA00` and neutral `330000`. No events were dropped and no reader fault
was reported. This supports targeting the USB keyboard collection without targeting the parent
controller, the QAM vendor collection or the I2C volume collection.

The maintainer authorized disabling that exact keyboard collection. Normal disable was refused
as a required system device; PnPUtil `/disable-device /force` accepted it but required a restart.
After the requested restart, Windows reported problem code 22 (disabled) for that exact keyboard
leaf. The maintainer confirmed that holding the left OEM button no longer opened Task View or
sent Win+Tab. The other controller HID collections, XInput interface and I2C volume collection
still enumerated healthy.

The maintainer then completed the post-reboot front-tap/volume capture. Three quick front-left
presses each delivered the Guide HID notification and its bounded synthetic release. Three quick
front-right presses each delivered `3781FF` / `370000` and matching QAM edges. No injected Win+G
was observed during those steps. Both volume buttons were exercised three times and delivered
their respective `33E900` / `33EA00` consumer reports followed by neutral `330000`, plus the
corresponding Windows volume-key edges. There were no reader faults or dropped events. These
observations verify that short front OEM input and volume input survive the exact keyboard disable;
they do not establish new acceptance for unexercised controls or live WSGM/Steam routing.
Disabling this keyboard also removes its F22 hold signal; do not claim native right-long support
while that lane is disabled. This device differs from the Claw's shared firmware PS/2 keyboard:
its volume buttons have an independently attributed I2C source, so disabling i8042prt or kbdhid
globally, the composite USB parent, or the I2C device is unnecessary and outside this action.

The nine recorded Acer service startup settings remained Disabled and the services Stopped after
reboot. Handheld.Widget nevertheless reappeared in the user session, so service/task changes alone
do not prove every OEM application stays absent across sign-in. FSE startup was disabled through
HKCU GamingConfiguration / StartupToGamingHome = 0 and verified again after restart; Explorer
remained the configured shell. The original service/task/FSE states and the exact keyboard-enable
command were saved locally before the changes.

The follow-up disabled the remaining UEIP service/tasks and both exact Predator Sense MSIX packages
using the elevated PackageManager Disabled flag. Explicit desktop/widget activation attempts then
returned 80073D5A with PID 0, both packages reported Disabled/NotAvailable, and no Acer software
process remained. An opt-in local maintenance task rechecks the closed target set at sign-in and
every five minutes, including replacement versions of those package identities. Its first run
completed successfully; it is not an application/runtime feature. The operational reference above
lists every target, proof limit and restoration step.

Automatic WSGM keyboard suppression and runtime F22/long-press routing remain unimplemented. The runtime contribution
in this increment owns rear-button mode; it does not yet automate the OEM software comparison
or the keyboard disable. Recovery scripts and private captures remain local artifacts.

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

HardwareVerified remains false. Runtime front-long handling, View/Menu attribution, full left-stick travel,
positive-axis motion, perceptual haptics, held-button disconnect, actual Windows sleep/wake and visible Steam
acceptance were not established by autonomous software trials. Library suspend/resume is not an OS
sleep test. Builds, fixtures and Overlay replays do not replace those observations.
