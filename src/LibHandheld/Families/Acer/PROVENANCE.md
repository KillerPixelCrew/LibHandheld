# Acer Predator Atlas 8

[OEM input and software control](OEM_INPUT_AND_DISABLE.md) is the complete operational reference:
physical source/report mapping, rear-mode acquisition/restoration, software disable targets,
post-reboot evidence, persistent package activation blocking and recovery.

[Power-limit tests and runtime bounds](POWER_LIMIT_TESTS.md) distinguishes advertised limits,
accepted profile values and measured load power, including the unresolved sustained ceiling.

[Motion tests and controller modes](MOTION_TESTS.md) records Windows cadence/resolution evidence,
DInput and controller-off topology, and the limits of the tested raw gyro paths.

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
the controller lifecycle as described in the attended follow-up below; it does not rewrite profile
mappings.

## Boundaries and acceptance

The initial contribution supplied exact detection, standard controller/rumble routing, OEM button
decoding and stock gamepad glyphs. The on-device implementation below adds EC power, fan control and
RPM, battery protection, Windows motion and volatile controller lighting. Optional Intel processor
settings remain owned by the shared processor service and use the signed PawnIO IntelMSR module.

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

| Area     | Mechanism and observed result                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Power    | I2C HID 1025:174B, FF05:0001, 65-byte feature reports, ID A0; EC data version 0800. Firmware advertises PL1 10..40 W and PL2 maximum 65 W. Both 20/40 W and unified 20/20 W Manual commands read back correctly. Release restored the original 25/45 W manual profile and active Turbo mode. Uninterpreted profile fields were retained.                                                                                                                                                                                                                                                                                                                      |
| Fans     | Health mask 02CD discovers SYS and SYS2. AcerGamingFunction WMI supplies Auto, Max and fixed-speed commands. EC health indices 4 and 8 supply the two RPM readings; index 1 supplies CPU temperature (53 degrees C in the final lifecycle trial). A bounded Max trial increased readings from 2829/2807 to 3281/3237 RPM. Release sent Auto. Behavior getters returned zero, so original behavior was unknown and restoration was reported unverified.                                                                                                                                                                                                        |
| Battery  | BatteryControl.GetBatteryHealthControlStatus and SetBatteryHealthControl, battery 1, function mask 1. Optimized charging toggled from disabled to enabled, read back enabled, then restored disabled with a clear journal. Calibration and smart-charge schedules are not invoked. No arbitrary percentage ceiling or bypass is claimed.                                                                                                                                                                                                                                                                                                                      |
| Motion   | Windows exposes both gyrometer and accelerometer through WinRT and the legacy Sensor API. Fresh samples reached canonical controller frames. The standard landscape Windows frame maps to application (X, Z, -Y); physical positive-axis trials remain outstanding.                                                                                                                                                                                                                                                                                                                                                                                           |
| Lighting | Controller FFA0:0001, 64-byte reports. Output ID 0F requests profile 0; input ID 10 returns RAM profile 1. Only the 883-byte lighting section at offset 586 is addressable. Brightness, speed, solid/breathing effects and ring colors use RAM command 21. The eight visible segments are mapped below; the ninth wire slot is retained. Brightness 50 -> 45, both effects, color 123456 and speed 10 read back correctly; release restored every lighting byte. The dedicated command input queue is flushed before snapshots to discard stale replies. No mapping or ROM command is sent. Lighting acquisition currently admits measured release 0100 only. |
| Lifetime | Physical controller and motion samples, four OEM descriptors, controller disable/re-enable, library suspend/resume and clean stop were observed. A bounded 10% rumble command was followed by an explicit zero; no human perception is claimed.                                                                                                                                                                                                                                                                                                                                                                                                               |

## Attended RGB slot mapping, 2026-10-27

The maintainer observed a retained-profile RAM preview with one static keyframe at 60% brightness.
Its nine RGB slots were red, green, blue, yellow, cyan, magenta, orange, violet and white, in order.
The original lighting section was journaled before dispatch; readback matched the preview bytes. The
preview has a ten-minute limit and restores the original lighting section afterward.

With the screen facing the user, the maintainer identified the left stick ring using the palette,
then confirmed each of the eight segments individually with white while all other slots were dark:

| Physical position | One-based RGB slot | Zero-based RGB index | Observed color  |
| ----------------- | ------------------ | -------------------- | --------------- |
| Top left          | 7                  | 6                    | Orange, FF8000  |
| Top right         | 6                  | 5                    | Magenta, FF00FF |
| Bottom left       | 8                  | 7                    | Violet, 8000FF  |
| Bottom right      | 5                  | 4                    | Cyan, 00FFFF    |

The right ring was then mapped one white slot at a time:

| Physical position | One-based RGB slot | Zero-based RGB index |
| ----------------- | ------------------ | -------------------- |
| Top left          | 2                  | 1                    |
| Top right         | 1                  | 0                    |
| Bottom left       | 3                  | 2                    |
| Bottom right      | 4                  | 3                    |

Only slot 9 was set to white in the final step. The maintainer reported no visible light and no
additional RGB hardware. This slot remains unassigned; no visible output does not establish its
firmware meaning. Runtime color/effect edits preserve its RGB bytes in the retained keyframes. The
tests restored the complete original lighting section with verified readback and a healthy recovery
journal. No ROM, controller mappings or calibration were changed.

The runtime exposes a shared "All ring segments" color and eight individual `lighting.color`
instances: `left.top-left`, `left.top-right`, `left.bottom-left`, `left.bottom-right`, and the
corresponding `right.*` names. Each individual command changes only its measured RGB triplet in the
active frame. Shared color affects the eight visible slots and retains the ninth. Colors,
brightness, speed and effect selection use the same native backend on both WSGM surfaces. Effects
and brightness remain shared across the rings; independent per-ring timing was not established. The
physical mapping is verified for this controller release, not every firmware.

## Attended fan control, 2026-10-27

With Acer OEM software disabled, the maintainer confirmed each physical fan separately, screen
facing the user. The EC health mask was 02CD. Native `sys` is the LEFT fan, health/RPM index 4;
`sys2` is the RIGHT fan, health/RPM index 8. The runtime now labels these Left and Right for that
measured topology; other health masks retain generic labels.

| Trial                       | Observed response                                                               | Maintainer confirmation                                                   |
| --------------------------- | ------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| Left Full Speed, right Auto | Left rose from about 2844 to 5500 RPM; right stayed about 2800 RPM              | Left fan accelerated and worked                                           |
| Right Full Speed, left Auto | Right rose past 4100 and settled around 5500 RPM; left slowed to its Auto speed | Right fan accelerated and worked                                          |
| Left fixed 50%, right Auto  | Left settled around 4100 RPM; right around 2750 RPM                             | Left was quieter than Full Speed                                          |
| Right fixed 50%, left Auto  | Right remained around 2750 RPM, close to its Auto baseline                      | Command accepted; no separate audible attribution claimed at this setting |
| Right fixed 75%, left Auto  | Right rose to about 4240 RPM; left stayed about 2775 RPM                        | Right accelerated and worked                                              |

Duty percentages are firmware control values, not a linear promise about RPM. Readings during a mode
transition briefly fell below their settled values before ramping; first snapshots are not
steady-state measurements. CPU temperature during the trial was approximately 39..45 degrees C. No
zero-duty/stop, exhaustive percentage sweep, fan curve or thermal-load acceptance is claimed.

Commands use `root\\WMI` / `AcerGamingFunction`, without running Acer services. The trial was
elevated; the non-elevated post-reboot token received Access Denied even when querying this class.
`SetGamingFanBehavior` selects one fan with its selection mask and behavior field;
`SetGamingFanSpeed` uses `(percent << 8) | selector`. The measured selectors are 2 for left and 3
for right. Auto=1, Full Speed=2, Custom=3. Native commands publish accepted values and do not
require a getter to write.

At the end, the journal restored the originals and release returned Idle with a healthy, empty
journal. Independent elevated getters then confirmed Auto for both: left `gmOutput=0x400`, right
`gmOutput=0x1000`, status 0 and decoded behavior 1. This later verified restore differs from the
initial trial's unreadable original behavior. The unknown-original Auto fallback remains necessary
and must still report unverified cleanup when an original/getter is unavailable.

## Attended rumble calibration, 2026-10-27

The maintainer completed Device Lab 0.1.2's existing rumble workflow on this same machine, with
source revision `dd6143daf330`. Report `Device Lab report 2026-10-27 1924.wsgmlab` has SHA-256
`98425C3614E5EB3778913C5A91A3DF41A0A2B6092506F22A36820D5756F151D0`. Its rumble stage is Completed;
identity was not completed, so this is continuity with the local Atlas investigation, not a fresh
board/BIOS identity attestation. The Windows.Gaming.Input route description independently names
controller `0502:F901`.

XInput slot 0 was felt. Channel 0 was identified at the left grip, channel 1 at the right grip; no
channel swap is needed. The live sliders marked the lowest felt level at 2 percent for each side.
Both sides were felt at the shortest offered pulse, 5 ms, at full strength and at the marked
2-percent strength. The first left full-strength 5 ms trial was marked not felt, followed by a
separate felt 5 ms trial. Left minimum-strength 5 ms was felt after two replays, right after one
replay. These are attended perceptual observations, not proof of hardware response below 5 ms or
guaranteed detection on every attempt. Longer pulses through 500 ms were also felt on both sides.

Windows.Gaming.Input's standard route was marked not felt in the route comparison, so this report
supports keeping the existing XInput output path. It does not establish a DInput output protocol.
The report contains 393 successful writes, no write errors, no zero failures and no remaining
vibrating routes. The live slider session reported no stop failure, and each route's final stage-end
zero succeeded. Target removal, disconnect and OS sleep/wake output cleanup remain separate
lifecycle acceptance.

The Atlas controller profile now publishes `MinimumStartIntensity = 0.02` and `MinimumPulse = 5 ms`
through the existing standard controller service. Both low/high channels remain native; trigger and
native-pulse channels remain unsupported. WSGM's existing output router applies the floor and
minimum pulse to bounded events only, preserving continuous rumble amplitudes. The shared default
60-frame/s pacing was not measured by this trial and is unchanged. Other standard controller
profiles retain their prior zero floor/pulse defaults. This calibration has not yet been exercised
through a deployed WSGM/Steam session.

Local offline inspection used Device Lab's `report` reader and the bounded JSON evidence entries
`rumble-calibration.json`, `rumble-manual.json`, `rumble-routes.json` and `rumble-writes.json`. The
37 Acer tests passed with Release compilation and no warnings. Full WSGM application compilation
stopped at the unchanged native backdrop target because `pwsh` was unavailable (exit 9009); it is
not an application build pass.

## Attended button follow-up, 2026-10-27

The maintainer completed the 27-step CLI capture on the same BIOS V1.03 / controller 0100 unit. The
front-left extra button delivered `9E0000050000000000000000000000` three times; these are Guide taps
with synthetic 200 ms release, not measured holds. The front-right extra delivered `3781FF` /
`370000` three times, a roughly 100 ms QAM pulse. Physical long holds were not tested. Face buttons,
D-pad and its up-right diagonal, shoulders, stick clicks, both full-range triggers and A+B+LB
rollover registered. View/Menu came through opposite the step names, and the left stick's leftward
travel reached only -0.133; these need an isolated confirmation before altering mapping or
calibration. The right stick reached both extrema. No reader faults or overflow occurred.

The original rear mode was XInput (1) / Macro (0), which produced no rear reports. Acer's installed
DeviceProxyService uses command 26 hex to read gamepad mode and command 24 hex to select the gamepad
and M-key modes. The response is report 10 / command 27 hex; its bytes 5 and 6 describe those two
modes. Selecting XInput (1) / Combination (1) exposes native rear reports without editing profile
mappings, calibration, lighting, other feature settings or ROM. The runtime journals the original
mode before dispatch, acquires this mode only with controller management, and restores the original
on release, disable or suspend. No user setup in Predator Sense is required.

The maintainer then completed the three-step managed rear capture. Rear LEFT delivered
`9A0400000000000000000000000000`, rear RIGHT `9A0200000000000000000000000000`, each with three
presses and `9A00...` releases. Both together delivered `9A06...`; partial release retained the
other button. Unrelated `9B00000080FF7F0080FF7F00000000` reports did not release held rear buttons.
The existing M1/bit 02 and M2/bit 04 protocol labels remain; measured physical left maps to M2,
right to M1. There were no reader faults or dropped events. Mode acquisition was Owned and release
Idle with verified restoration to the original XInput/Macro mode. This proves rear acquisition and
release on this unit, independently of the normal user-configured profile.

### Front shortcuts and device attribution

The subsequent six-step front capture showed the left short Guide HID report followed about 200 ms
later by injected LeftWin+G. Left long presses produced a complete, non-injected LeftWin-down /
Tab-down / LeftWin-up / Tab-up sequence, with no Guide HID notification. Right holds produced
non-injected F22 down/repeat/up; these differ from the shorter QAM HID taps. The keyboard hook alone
cannot identify a physical source or distinguish this clean Win+Tab from the same keys on another
keyboard.

With the maintainer's direction, the Acer user-mode software stack was stopped and its restart
entries disabled for comparison. The maintainer confirmed that Win+G and the Predator Sense launch
disappeared, while Win+Tab persisted. Native EC power, fans, battery, lighting, controller, motion
and OEM services still acquired and stopped cleanly with those applications absent.

The four-step Raw Input capture then attributed both Win+Tab and F22 to the controller's USB
keyboard collection `HID\\VID_0502&PID_F901&MI_02&Col01`, serviced by `kbdhid`, usage 0001:0006. The
volume buttons instead used the separate I2C consumer collection `HID\\1025174B&Col08`, with reports
`33E900` / `33EA00` and neutral `330000`. No events were dropped and no reader fault was reported.
This supports targeting the USB keyboard collection without targeting the parent controller, the QAM
vendor collection or the I2C volume collection.

The maintainer authorized disabling that exact keyboard collection. Normal disable was refused as a
required system device; PnPUtil `/disable-device /force` accepted it but required a restart. After
the requested restart, Windows reported problem code 22 (disabled) for that exact keyboard leaf. The
maintainer confirmed that holding the left OEM button no longer opened Task View or sent Win+Tab.
The other controller HID collections, XInput interface and I2C volume collection still enumerated
healthy.

The maintainer then completed the post-reboot front-tap/volume capture. Three quick front-left
presses each delivered the Guide HID notification and its bounded synthetic release. Three quick
front-right presses each delivered `3781FF` / `370000` and matching QAM edges. No injected Win+G was
observed during those steps. Both volume buttons were exercised three times and delivered their
respective `33E900` / `33EA00` consumer reports followed by neutral `330000`, plus the corresponding
Windows volume-key edges. There were no reader faults or dropped events. These observations verify
that short front OEM input and volume input survive the exact keyboard disable; they do not
establish new acceptance for unexercised controls or live WSGM/Steam routing. Disabling this
keyboard also removes its F22 hold signal; do not claim native right-long support while that lane is
disabled. This device differs from the Claw's shared firmware PS/2 keyboard: its volume buttons have
an independently attributed I2C source, so disabling i8042prt or kbdhid globally, the composite USB
parent, or the I2C device is unnecessary and outside this action.

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

The subsequent WSGM `OtherManagers` integration adds exact-Atlas Full-mode takeover of those ten
services, three tasks, two MSIX families and the dedicated controller keyboard. It records changes
before dispatch, propagates restart requirements to setup, rechecks accepted policy at startup and
every five minutes, and restores recorded changes on uninstall. This is host application policy;
LibHandheld continues to own rear-button mode and physical device protocols. The implementation is
awaiting installation and live runtime acceptance; the historical local guard is not that proof.
Runtime F22/long-press routing remains unsupported while the keyboard source is disabled. Recovery
scripts and private captures remain local artifacts. The operational reference above distinguishes
the two ownership/recovery paths.

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

The subsequent six-pose/three-rotation attended trial confirmed both Windows motion axis mappings as
`(X, Z, -Y)`. See [motion evidence](MOTION_TESTS.md) for the measured signs, hand-positioned
rotation integrals and unresolved accelerometer magnitude asymmetry; axis acceptance is not scale
calibration.

HardwareVerified remains false. Runtime front-long handling, View/Menu attribution, full left-stick
travel, precise motion scale calibration, held-button disconnect, actual Windows sleep/wake and
visible Steam acceptance were not established by autonomous software trials. Library suspend/resume
is not an OS sleep test. Builds, fixtures and Overlay replays do not replace those observations.

## Follow-up: presets and signed processor access (2026-10-27)

The sustained-limit descriptor now publishes Quiet, Balanced and Turbo as firmware-managed presets.
Their zero watt fields are unspecified placeholders, never commands. Hosts must select the declared
AC/DC scenario and Windows mode without subsequently writing PL1/PL2. Full Power selects Manual with
the firmware-advertised sustained/boost maxima (40/65 W on this unit) and Windows Best performance.
The host retains Custom and per-game/AC/DC assignment policy.

Windows blocked KX's HwRwDrv.x64; a later read attempt returned certificate-revocation
error 2148204812. Atlas no longer requires IntelKx: optional voltage settings use the existing
pinned, signed PawnIO IntelMSR module's allowlisted MSR 0x150 mailbox. Neutral 0 mV commands were
accepted on Core, Cache, GPU and System Agent on this device. This proves command acceptance, not a
measured voltage change or acceptance of nonzero offsets. GPU-clock MMIO writes remain unavailable
because the signed MCHBAR module has no write export. Other device families retain their existing
backend. Mailbox commands dispatch once, check busy/completion status, and never retry uncertain
writes. Module source: https://github.com/namazso/PawnIO.Modules/blob/main/IntelMSR.p

Rear paddle descriptors carry no extra Guide/QAM forwarding marker. Physical RearPaddle1/2 states
still arrive through canonical controller input. This resolves the WSGM host rejecting the entire
OEM descriptor set when a rear enum occupied the Guide/QAM-only forwarding field. Front long-press
keyboard events remain suppressed by the separately disabled keyboard leaf described above.

## Stock-profile limit observations (2026-10-27)

A read-only follow-up queried command 0002/get (payload [00 A0 02 00 02 selector]) for selectors 0,
1, 2 and 3. Firmware echoed each selector at offset 5. Observed PL1/PL2 values at offsets 6/10 were
35/65, 40/65, 30/65 and 40/65 W respectively. These are observations on this unit, not a hard-coded
stock wattage table. Mode 3 was active during the inventory. The old service queried only Manual and
withheld numeric power in every stock mode, which explains the first-launch unknown TDP. ReadCurrent
now keeps Manual bytes for edits while publishing limits read from the active stock profile. Missing
getters still do not gate writes.

## Live fan scheduling correction (2026-10-27)

The 1779 runtime exposed a brief stale-temperature warning every ten seconds despite valid CPU
samples and working fan writes. The common serializer's full maintenance branch skipped its
read-only telemetry hook, creating a four-second sensor gap against the host's three-second fan
window. Every observation turn now invokes the telemetry hook after any maintenance and before
publication. Maintenance remains on its ten-second schedule; temperature freshness limits and
firmware-auto fallback are unchanged. A focused regression forces a maintenance turn and asserts
maintenance/telemetry/publication order. Runtime acceptance must span multiple maintenance turns.

## Power readout after a mode selection (2026-10-27)

The initial stock-profile observation fix did not cover a later scenario command: selecting a stock
mode still cleared both power observations, and control values are not periodically polled. Mode
commands now best-effort read the selected profile, then publish that pair immediately after
firmware accepts the mode. Manual selection publishes its own saved pair rather than retaining the
previous stock limits. Getter failure never blocks a mode write and leaves its numeric limits
unknown. Two focused regressions cover mode changes and unavailable getters.

## Rejected auxiliary HID cloaking (2026-10-27)

An isolated Steam SDL inventory identified four extra Xbox entries as F901 MI_01/COL01 and
MI_02/COL03, COL04, COL05. Hiding those auxiliary leaves reduced enumeration from five pads to one
Steam Deck, but the maintainer then lost both physical and RustDesk keyboard input. Turning cloaking
off, removing the entries and stopping WSGM did not restore input; uninstalling HidHide did. That is
a failed hardware trial, regardless of its initial controller-list result.

The auxiliary-hide profile changes were withdrawn. Do not add these vendor/consumer/OEM HID
collections to the kernel hide set. Keep HidHide scoped to the actual XInput controller nodes. Any
suppression of SDL's auxiliary Xbox identities must happen inside the consuming application, without
blocking Windows keyboard, mouse, consumer or OEM collections.

## DInput rumble discovery (2026-10-27)

A focused F90A capability query found gamepad input report 01 (64 bytes), output report 05 (32
bytes, vendor pageFF00), and 17 button usages. A rumble-only report 05 with byte1=01, right/high
amplitude at4 and left/low at5 produced a pulse the maintainer explicitly felt. Accelerometer RMS
increased from 0.00324 g idle to 0.04231 g during the 600 ms pulse. A zero-amplitude report stopped
it, and the original XInput/rear-mode tuple0100000100 was restored and verified. This establishes a
DInput rumble path; it does not establish motor-side mapping or the complete DInput OEM-button map.
A subsequent button capture recorded no transitions because the maintainer had left the device and
was remote-only. Do not infer missing buttons from that empty capture. Physical button acceptance
remains pending while source/descriptor investigation continues.

## DInput runtime integration (2026-10-27)

Runtime controller ownership now selects DInput 2 / Combination 1 and returns the exact original
mode pair on release. The command lane follows the same physical port from F901/FFA0:1 to
F90A/FFF0:40 after one mode dispatch; it waits for enumeration without retrying the write. Input
uses the generic descriptor parser for the F90A gamepad. The actual gamepad leaf and old XInput
gamepad nodes form the hide set; auxiliary HID collections are not hidden. Report05 rumble uses an
independent output handle and sends zero before releasing it. Lighting continues through the
correlated command lane. Windows IMU remains the motion source.

An on-device library lifecycle run reached Active with every service Owned, produced 128 controller
samples in one second, accepted rumble and zero output, disabled/re-enabled controller management,
suspended/resumed, and stopped Clean. All mode changes restored correctly. The generic parser uses
the 17-button descriptor and the OEM PS/camera/FL1/FR1 naming for face/OEM mapping. Those physical
button mappings still require attended acceptance; the empty remote-only capture is not evidence
that buttons are absent. No Steam executable, SDL setting or Windows keyboard filter is changed by
this route.

## Confirmed DInput companion hide set (2026-10-27)

After switching to F90A, two additional non-gamepad identities remained. Following the maintainer's
explicit request to hide those two, each was tested separately: the command collection FFF0:0040 at
MI_00/COL02 and the consumer collection000C:0001 at MI_01/COL03. The fresh SDL inventory became
exactly one Steam Deck. The maintainer confirmed keyboard input still worked, and confirmed the
Steam list after Steam restarted. This supersedes the earlier blanket prohibition for these two
measured DInput collections only; the failed F901 auxiliary set remains forbidden.

The library selects these exact release0100 report shapes on the selected gamepad's physical port.
The keyboard MI_01/COL01 and mouse MI_01/COL02 do not match. WSGM's existing ownership ledger adds
and restores the two entries. No Steam filtering or global backend override is involved.

## Atlas stock power ordering (2026-10-28)

The desktop application's enum calls 0 Turbo and 1 Balance, but the PA08-I51 V1.03 EC stock table
contains 35/65 W at index 0, 40/65 W at index 1, and 30/65 W at index 2. The maintainer observed the
resulting inverted Turbo/Balanced readouts. Atlas now names the measured power tiers Balanced 0,
Turbo 1 and Quiet 2. All three retain 65 W boost because that is what their firmware profiles
contain; no invented boost value or manual-limit write is added to a stock selection. Native service
tracing confirmed that the OEM path selects a complete profile and only writes numeric fields for
Manual 3. Originals continue to be stored as raw mode numbers, preserving exact restoration.

## Keyboard-interface hiding withdrawn (2026-10-28)

The deployed F90A companion set stopped all keyboard input again, physical and remote. Both failures
hid a collection on the keyboard's own USB interface: F901 MI_02/COL03..05 beside keyboard
MI_02/COL01, and the F90A consumer collection MI_01/COL03 beside keyboard MI_01/COL01. The consumer
collection is no longer a companion. The shared hide set now drops any identity whose USB interface
also carries a present keyboard or mouse collection, so no profile can repeat this. The MI_00/COL02
vendor collection shares an interface only with the gamepad and stays hidden; that remains
attended acceptance after HidHide is reinstalled.

Lighting edits are no longer restored on release. Restoring the journaled original brought the
firmware rainbow back every time WSGM stopped. Old lighting originals are retired unrestored.

## Explicit preset limits (2026-10-28)

The stock EC profiles keep PL2 at 65 W in all three tiers, and the platform's RAPL registers did not
follow them, so a stock selection changed only the EC's PL1 field and the TDP readout did not match
what the maintainer measured. At the maintainer's direction the Quiet, Balanced and Turbo presets
now write explicit PL1/PL2 through Manual 3: 20/20, 25/37 and 30/37 W, HandheldCompanion's Arc G3
(ClawCG3EM) table after its 20 W floor. Full Power keeps the firmware maximum, 40/65 W. Manual
limits were measured to bound package power (25 W requested, 24.6 W measured). The stock EC
profiles remain selectable through the scenario control.

## Follow-up: KX removed (2026-10-28)

KX and its HwRwDrv driver were removed from LibHandheld and WSGM. Every Intel model, not only the
Atlas, now uses PawnIO IntelMSR for processor settings, and the generic Intel package power path
writes MSR 0x610 through the same module. The Atlas keeps its native EC power path and does not use
that generic transport. Intel GPU clock control is no longer offered on any model.

## DInput input capture (2026-10-28)

A read-only attended capture of every 0502 collection, with Steam closed, measured the F90A gamepad
report 01. Triggers are analog Rx (left) and Ry (right), 0..255. Buttons 7/8 switch on at value 2,
so they no longer force a trigger to full. Rear-right is button 16 (M1) and rear-left button 17
(M2). L3/R3 are 11/12. The front OEM keys send nothing in any gamepad, vendor or consumer report:
a tap produces no event at all, a hold of front-left sends one Win+Tab pulse and a hold of
front-right sends F22 (VK 0x85, scan 0x6D) with auto-repeat and a release, both on the keyboard
interface. Predator Sense never selects DInput; its tap handling uses the XInput-topology vendor
reports. Firmware mode 3 ("Special") keeps the F901 XInput topology, including the IG_00 collection
and the auxiliary SDL identities: front-left tap 9E, front-right tap `37 81FF` on MI_02/COL04, rear
9A. The probe restored the original mode 1/0 with verified readback.

Motion upload in Special mode: after mode 3/1 was selected, command 2F 01 was sent once and the
device was held still, then pitched, yawed and rolled. No collection reported anything during
those phases, including the FFA0 command lane. Predator Sense defines but never calls this
command. Command 2F 00 and the original mode 1/0 were restored and verified. Windows sensors
remain the motion source.

## Back to XInput with verified auxiliary hiding (2026-10-28)

DInput was abandoned because front-key taps never arrive in it. The managed mode is now XInput 1
with rear Combination 1. With WSGM and Steam stopped and the maintainer's drivers freshly updated,
an attended trial hid F901 MI_01/COL01, MI_02/COL05, MI_02/COL04 and MI_02/COL03 one at a time.
After each step the maintainer typed a confirmation through the keyboard path, and again with all
four hidden. Steam then enumerated exactly one controller, the XInput pad. The profile lists these
four exact leaves as AuxiliaryHidCollections, the only identities allowed past the
keyboard-interface guard. WSGM remains allowed to read them for the command lane and QAM report.
The earlier keyboard losses remain unexplained; if they recur, remove this list first.
