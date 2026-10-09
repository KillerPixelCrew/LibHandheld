# Provenance

Everything in this package was built without an Ally on the desk. Every device fact comes from one
of the references below, and none has a recorded hardware pass yet. HC 1.3.1.6 is the primary
Windows reference, including button events. HHD is a cross-check and a source for behavior HC does
not implement. Each disagreement between them is written down here.

## References

| Reference                  | Revision                                                                       | Local path                             |
| -------------------------- | ------------------------------------------------------------------------------ | -------------------------------------- |
| Handheld Companion (HC)    | 1.3.1.6 Patreon build, decompiled (see `_ref/HandheldCompanion/PROVENANCE.md`) | `_ref/HandheldCompanion`               |
| HHD (Handheld Daemon)      | `5b49c5d904257e042a704ade958fac0ba57af4b1` (2026-09-14)                        | `_ref/hhd`                             |
| handheld-controller-glyphs | `46792aadf3b104efec1c5240ba414d2c0bf84127` (2026-07-21)                        | `_ref/handheld-controller-glyphs`      |
| Device Lab run on RC73XA   | `AllyXLab-20260915-171651-7db7c7`, BIOS RC73XA.317, EC 3.14                    | `src/WSGM.DeviceLab/Knowledge/Devices` |
| Ally X Lab (retired)       | `tools/AllyXLab`, removed in `829c5a5c`; read it from git history              | none                                   |

HC source paths are relative to `_ref/HandheldCompanion/source/HandheldCompanion/` and HC
`Resources/` paths to `_ref/HandheldCompanion/`; HHD paths are relative to `_ref/hhd/src/`, with
`rog_ally/`, `const.py` and `hid.py` short for `hhd/device/rog_ally/` and `__init__.py` under Power
for `adjustor/drivers/asus/__init__.py`. Line numbers are those revisions'.

## Identity

| Fact                                                                                  | Source                                                                                                         |
| ------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| Manufacturer `ASUSTeK COMPUTER INC.`, baseboard product RC71L, RC72LA, RC73YA, RC73XA | HC `HandheldCompanion.Devices/IDevice.cs:1027-1046` (`MotherboardInfo.Product`)                                |
| RC72L admitted for the Ally X                                                         | HHD `adjustor/core/const.py:339` (`ROG Ally X RC72L`); the earlier scaffold manifest                           |
| Model split: Ally, Ally X, Xbox Ally, Xbox Ally X                                     | HHD `hhd/device/rog_ally/__init__.py:116-142`; HC classes `ROGAlly`, `ROGAllyX`, `XboxROGAlly`, `XboxROGAllyX` |
| Controller USB IDs 0B05:1ABE and 0B05:1B4C                                            | HC `ROGAlly.cs:212-213`; HHD `rog_ally/base.py:29-30`                                                          |

HHD matches the DMI product name by substring; HC and this package match the baseboard product
exactly. The Xbox models' SMBIOS SKU was empty in the lab run, so the SKU is not part of the gate.

## Controller

| Fact                                                              | Source                                                                                                                         |
| ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| The pad is read through XInput on every model                     | HC `HandheldCompanion.Managers/ControllerManager.cs:2236-2243` (0B05 XUSB becomes `XboxAdaptiveController : XInputController`) |
| Guide bit through `XInputGetStateEx` (xinput1_4 ordinal 100)      | HC `HandheldCompanion.Controllers/XInputController.cs:232, 376-377`                                                            |
| Slot identity through `XInputGetCapabilitiesEx` (ordinal 108)     | HC `XInputController.cs:53-89, 370-371`                                                                                        |
| Rumble through `XInputSetState`, large motor left                 | HC `XInputController.cs:279-296`                                                                                               |
| Vendor collection: the last with 64-byte features answering 0x5A  | HC `ROGAlly.cs:416-436` (`IsReady`), events and tables alike; HHD uses FF31:0080 on Linux (`rog_ally/base.py:383-393`)         |
| Controller tables (game mode, button pairs, triggers, commit)     | HC `ROGAlly.cs:93-183`, byte for byte, in `ConfigureController` order (`ROGAlly.cs:646-668`)                                   |
| Tables sent as 64-byte feature reports                            | HC `ROGAlly.cs:646-668` (`WriteFeatureReport(..., 64)`)                                                                        |
| Tables restored with the factory M1/M2 block on release           | HC `ROGAlly.cs:396-400` (`ConfigureController(Remap: false)`) with HC's `M1M2Default`                                          |
| Xbox Ally X events arrive on FF31:0076 (MI_02 Col01)              | Device Lab RC73XA run 2026-09-25: `5A A6` Armoury Crate, `5A 93` Library, none on FF31:0080; consistent with HC's probe        |
| Xbox Ally X M1/M2 send F18/F17 with no table written              | Device Lab RC73XA run 2026-09-25, `back-left1` F18 and `back-right1` F17 (`RearKeysNative`)                                    |
| Front buttons also raise ATK WMI events 166 (0xA6) and 147 (0x93) | Device Lab RC73XA run 2026-09-25 (`AsusAtkWmiEvent`); unused, the HID report carries the code                                  |
| Holding Armoury Crate or Library repeats F21 or F22               | Device Lab RC73XA run 2026-09-25 (`oem-left-hold`, `oem-right-hold`)                                                           |
| Every table sent even when one is refused                         | HC `ROGAlly.cs:646-668` ignores each `WriteFeatureReport` result                                                               |

Disagreements:

- HHD writes the tables as HID output reports (`hid.py:357-372`), HC as feature reports. The RC73XA
  run found no output report on FF31:0080, so feature reports are used.
- HC's `dPadLeftRightDefault` and `faceButtonsABDefault` (`ROGAlly.cs:108-115, 131-138`) have a
  ten-byte third block where HHD's `REMAP_DPAD_LR` and `REMAP_AB` (`const.py:201-253, 549-605`) have
  eleven, and HC's `M1M2Default` stops before HHD's fourth block. HC's bytes and order are sent: an
  Xbox Ally X on 2026-09-25 refused the HHD-layout set (`HidD_SetFeature` failed) and left M1/M2
  dead, while HC ships its own set on every Ally.
- The retired Ally X Lab also drove the pad through Windows.Gaming.Input, which has no guide button.
  HC reads the Xbox button only through XInput's guide bit and has no other route: every Ally, the
  Xbox models included, is its XUSB pad on XInput, and so is this plugin's. The 0.2.1 RC73XA run
  that saw no XInput slot and no gamepad collection predates the lab's HidHide allowance (AllyXLab
  0.3.1), and a HidHide-cloaked XUSB pad reads exactly that way, so the run does not contradict HC.
- Commit values: HC sends vibration 100/100 and stick and trigger ranges 0-100
  (`ROGAlly.cs:177-183`); HHD sends vibration 50 on the Ally X family and outer limits of 0x40 or
  0x60 (`rog_ally/base.py:44-51`, `const.py:1073-1118`). HC's values are used.
- HHD re-sends the tables after five seconds in case the MCU missed them (`base.py:164-171`). An
  uncertain write is never retried here.
- HHD writes rumble as the HID output report `0D 0F 00 00 weak strong FF 00 EB` (`base.py:226-254`);
  HC uses XInput. HC is used.

## Buttons

| Fact                                                              | Source                                                                                    |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| 0xA6 is OEM1, 0x38 is OEM2, and 0x93 is separate Library/OEM5     | HC `ROGAlly.cs:53-83, 485-505`                                                            |
| 0xA7 and 0xA8 are M2/OEM4 press and release                       | HC `ROGAlly.cs:485-505`                                                                   |
| 0xA5 is ignored                                                   | HC maps it to OEM3 (M1) but `HandleEvent` never acts on it (`ROGAlly.cs:71-74, 485-505`)  |
| No front button reports a long press                              | HC press-and-releases 0xA6, 0x38 and 0x93 after `KeyPressDelay` (`ROGAlly.cs:485-505`)    |
| A press seen on both the vendor and the keyboard path counts once | This package: the first report of a press wins, the other transport's echo is dropped     |
| Release-less events are held for 200 ms                           | HC `KeyPressDelay` (`IDevice.KeyPressAndRelease`), the SDK's `OemButtonLatch`             |
| M1/M2 become keyboard keys only after the M1/M2 table is written  | HHD `const.py:897-954` (`REMAP_M1M2_F17F18`), `base.py:396-403`                           |
| With HC's M1/M2 table applied, left sends F17 and right F18       | HHD `base.py:396-403`; RC73XA tester with accepted tables, 2026-09-26                     |
| With no table written, left sends F18 and right F17               | Device Lab RC73XA runs (results 019/020; `back-left1`/`back-right1`), tables never landed |
| Xbox models' front buttons arrived as F21 (left) and F22 (right)  | Device Lab RC73XA run, results 017 and 018                                                |
| Xbox button is the Guide (XInput guide bit)                       | HC `XInputController.cs:376-377`; tester report 2026-09-25 (RC73XA, it opened the QAM)    |
| Xbox models: Library is Quick Access, Armoury Crate opens WSGM    | Maintainer mapping 2026-09-26; HC opens its window from it (`ROGAlly.cs:283`)             |
| Xbox models' left button is Armoury Crate, right is Library       | glyph theme `themes/asus/rog-xbox-ally.css`; HC maps 0x93 to Library (`ROGAlly.cs:63-66`) |

Disagreements:

- HHD sends the Xbox models' Xbox button to QAM (`share_to_qam`, `base.py:415-427`) and 0xA6 to the
  guide unless `swap_xbox` is set. That left Steam's main menu on the Armoury Crate button; the
  tester pressed the Xbox button for it and got the QAM, so HC's guide reading is used.
- HHD merges 0x93 into QAM and treats 0xA7 as the right button's hold (`base.py:180-215`). HC's
  Windows event semantics are used: 0x93 stays Library and 0xA7/0xA8 form M2 press/release.
- Both lab runs saw the left rear button send F18, but in both the tables went to FF31:0080, which
  refuses them, so that is the firmware's own assignment. Once HC's table was accepted on FF31:0076,
  the tester found those sides swapped: with the table, F17 is left and F18 right, as HHD reads the
  same bytes. HC labels F18 as M1 (`ROGAlly.cs:263-282`) and leaves the sides to the user's layout.
- The glyph theme labels the left rear button M2 (`themes/asus/rog-ally.css`, `--button-l4-image`).
  This package labels left M1, following HC and the lab wizard. The physical print is unverified.

## Motion

| Fact                                                              | Source                                                                              |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| WinRT `Gyrometer`/`Accelerometer` first, legacy Sensor API second | HC `HandheldCompanion.Devices/IDevice.cs:1152-1172`                                 |
| Standard legacy fields, format 3F8A69A2..., PIDs 2-4 and 10-12    | HC `HandheldCompanion.Sensors/WindowsSensorManager.cs:96-114`                       |
| Axis swap `{X: X, Y: Z, Z: Y}`, Ally and Ally X signs (-1, -1, 1) | HC `Resources/Devices/ROGAlly.json`, `ROGAllyX.json`                                |
| Xbox models: gyro signs (1, 1, -1), accelerometer (-1, -1, 1)     | HC `Resources/Devices/XboxROGAlly.json`, `XboxROGAllyX.json`                        |
| HC's matrix output equals WSGM's motion basis                     | HC `ClawA2VM.json` gives `(raw X, raw Z, -raw Y)`, the Claw plugin's measured basis |

Disagreements: HHD maps all four models with one transform for both sensors, raw y to output z and
raw z to output y negated, that is `(x, -z, y)` (`rog_ally/base.py:34-42`), and reads the IIO device
directly with its own scale. HC is used, and its Xbox gyro signs differ from its accelerometer
signs, which a single IMU would not normally need; the lab report must settle it. The stationary
gyro-offset correction is the SDK's shared one, with thresholds measured on the Claw's LSM6DSO.

## Power

| Fact                                                                    | Source                                                                                                                  |
| ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `\\.\ATKACPI`, IOCTL 0x0022240C, DSTS/DEVS layout                       | HC `HandheldCompanion.Devices.ASUS/AsusACPI.cs:11-51, 110-188`                                                          |
| SPL 0x001200A3, SPPT 0x001200A0, FPPT 0x001200C1                        | HC `AsusACPI.cs:45-49`                                                                                                  |
| Long limit writes SPL; short limit writes SPPT and FPPT together        | HC `AsusACPI.cs:343-352`                                                                                                |
| Performance mode 0x00120075: 0 performance, 1 turbo, 2 silent           | HC `AsusACPI.cs:51` and `ROGAlly.cs:229-259` (`OEMPowerMode`); HHD `adjustor/drivers/asus/__init__.py:296-303`          |
| Write order keeping SPL <= SPPT <= FPPT                                 | Retired Ally X Lab `AsusControl.Restore` (removed in `829c5a5c`); HHD writes fast, slow, steady (`__init__.py:384-389`) |
| 100 ms between limit writes, 150 ms after a mode change                 | HHD `__init__.py:14` (`TDP_DELAY`); the retired Ally X Lab                                                              |
| DSTS scalar presence bit 0x10000                                        | HC `AsusACPI.cs:182-188` (`DeviceGet` returns raw - 65536)                                                              |
| Watt ranges: Ally and Ally X 5-30, Xbox models 5-35                     | HC `ROGAlly.cs:215`, `XboxROGAlly.cs:14`, `XboxROGAllyX.cs:14`; HHD `adjustor/core/const.py:261-320`                    |
| Presets Silent/Performance/Turbo 10/15/25 W (Ally), 13/17/25 W (others) | HC `ROGAlly.cs:229-259`, `ROGAllyX.cs:13-27`, `XboxROGAlly.cs:17-31`, `XboxROGAllyX.cs:17-31`                           |

Disagreements:

- HC's Xbox Ally range is 15-35 W, the same as its Xbox Ally X, while HHD lists 4-20 W (SPPT 20,
  FPPT 25, OC 25; `adjustor/core/const.py:292-305`). HC's Windows envelope is used: HHD's 20 W would
  cap the device well below what it runs at under Windows, and the BIOS enforces its own limit. HC's
  Silent preset on both Xbox models is 13 W, so the minimum is lowered to 5.
- A limit DSTS reports outside 1-80 W is unknown. The Xbox Ally X (RC73XA) reports 0 W for all three
  limits and no performance mode (tester log, 2026-09-25).
- HHD's performance preset is 30 W on AC and 25 W on battery for the Ally and Ally X; HC uses 25 W.
  HC is used.
- HC never reads the limits back and writes them blind (`ROGAlly.cs:694-702`); so does this plugin
  when DSTS cannot report them. Where DSTS does report them (the Ally X Lab), the originals are
  journalled and restored on stop, and a write that reads back is verified.

## Fans and charging

| Fact                                                            | Source                                              |
| --------------------------------------------------------------- | --------------------------------------------------- |
| Curve IDs CPU 0x00110024, GPU 0x00110025, mid 0x00110032        | HC `AsusACPI.cs:31-35`                              |
| Sixteen bytes, eight temperatures then eight duties, duty <= 99 | HC `AsusACPI.cs:281-298`, `ROGAlly.cs:287-311`      |
| Same curve on every fan                                         | HC `ROGAlly.cs:313-327`                             |
| Automatic writes HC's default tables                            | HC `ROGAlly.cs:185-195, 466-478`                    |
| Curve readback selector by performance mode                     | HC `AsusACPI.cs:300-314`, which nothing in HC calls |
| A refused DSTS query reads as zeros, not an error               | HC `AsusACPI.cs:98-108` ignores the IOCTL result    |
| Mid fan written even when its curve cannot be read              | HC `ROGAlly.cs:317-321`                             |
| Fan readings 0x00110013 and 0x00110014, read as duty            | HC `AsusACPI.cs:39-41, 316-333`                     |
| Charge limit 0x00120057                                         | HC `AsusACPI.cs:37, 335-341`                        |

Disagreements: HHD drives two fans through Linux hwmon with fixed temperature points 30-100 °C and a
0-255 duty (`__init__.py:46-48, 90-113`); HC's ATKACPI curve is used. When the plugin has captured
the original curves it writes those back for "automatic" instead of HC's fixed defaults. The charge
limit is offered from 40 %, not HC's 0.

## Lighting

| Fact                                                                        | Source                                     |
| --------------------------------------------------------------------------- | ------------------------------------------ |
| Aura collection found by feature report 0x5D                                | HC `ROGAlly.cs:416-436`                    |
| Brightness `5D BA C5 C4 level` as a feature report, level = percent / 33.33 | HC `ROGAlly.cs:507-522`                    |
| Colour message, apply `5D B4`, set `5D B5` as output reports                | HC `ROGAlly.cs:556-617`                    |
| Effects solid 0, breathing 1, colour cycle 2, rainbow 3; zones 0-4          | HC `ROGAlly.cs:22-51`; HHD `hid.py:41-130` |
| Speed bytes and HC's bands (33/66)                                          | HC `ROGAlly.cs:31-36, 552`                 |
| One colour or an effect: one all-zone message, apply, set                   | HC `ROGAlly.cs:555-572` (`ApplyColor`)     |
| Two ring colours: four zone messages at slow speed, no apply or set         | HC `ROGAlly.cs:574-593` (`ApplyColorFast`) |
| Xbox models: lamp array to autonomous mode (`06 01`)                        | HHD `rog_ally/base.py:482-505`             |

Disagreements:

- HHD sends Aura as report 0x5A on the vendor collection, with an "ASUS Tech.Inc." handshake first
  (`const.py:1151-1177`); HC uses report 0x5D without a handshake. HC is used.
- Speed: HC calls 0xEB slow, 0xF5 medium and 0xE1 fast; HHD calls 0xE1 low and 0xF5 high
  (`hid.py:92-100`). HC is used.
- The lamp array step is HHD's only; HC does nothing special on the Xbox models. HHD writes it
  through hidraw, an output report, and only on the Xbox Ally X. Report 6 is the HID
  LampArrayControlReport, a Feature report by specification, and the RC73XA run saw only feature
  reports on page 0x59. This package sends it once per cycle on both Xbox models, as a feature
  report when the collection has one and as output otherwise, and never retries it.
- HC uses `ApplyColorFast` only for its ambilight mode. It is used here whenever the two rings
  differ, because it is HC's only path that addresses the rings separately.

## Glyphs

Two profiles from handheld-controller-glyphs: `rog-ally` for RC71L and RC72LA with the
`asus/rog-ally` artwork, and `rog-xbox-ally` for RC73YA and RC73XA with `asus/rog-xbox-ally`. Guide
and QAM follow `themes/asus/rog-ally.css` and `themes/asus/rog-xbox-ally.css`, except that the Xbox
models' Guide is the Xbox button, which the theme has no artwork for, so it keeps Steam's generic
guide glyph; the theme draws Armoury Crate there because it follows HHD's routing. All ASUS assets
are shipped, including the mono face buttons and the alternate rear-button marks, unmodified. No
highlight overlays are declared: their coordinates would be guesses.

## What the Device Lab report must confirm

Everything below except the DSTS readback in items 9 and 10 is established by HC; a report confirms
it. Readback is the one question HC cannot answer, because it never reads the power limits and never
calls its curve getter.

1. SMBIOS baseboard manufacturer and product on each model, and the controller's USB product ID.
2. That the pad has an XInput slot reporting VID 0B05 through `XInputGetCapabilitiesEx` on every
   model, as HC expects, with the tester's HidHide allowance in place, and which XUSB or XInput HID
   nodes must be hidden.
3. That the guide bit reports the Xbox button on the Xbox models, and nothing on the others.
4. Which vendor codes each front button sends (0xA5, 0xA6, 0x38, 0x93, 0xA7, 0xA8) after the tables
   are written, and whether the Xbox models then send F21/F22 as well; a button on both paths is
   counted once.
5. That HC's feature-report controller tables are accepted on each model and by which collection,
   which rear button sends F17 and F18, and the physical M1/M2 print on each side. The Xbox Ally X
   refused the earlier HHD-layout set on FF31:0080 (2026-09-25).
6. That writing the factory tables returns the rear buttons to their stock behaviour.
7. Rumble strength through XInput, and whether 100/100 vibration intensity is too strong on the Ally
   X family as HHD claims.
8. Gyro and accelerometer axis signs per model, and whether WinRT or the legacy API delivers them.
9. Whether DSTS reports SPL, SPPT, FPPT and the performance mode on the other models (RC73XA reports
   0 W and no mode), whether a mode change resets the limits, and what envelope each Xbox model's
   firmware accepts.
10. Whether a DSTS curve read returns the curve in force or the factory table for the mode (RC73XA
    refuses the query), whether HC's uncalled curve selector (1 and 2 swapped) addresses the right
    profile, whether the mid fan exists, and what unit the fan readings use.
11. The charge-limit range the firmware accepts, and whether DSTS reads it back.
12. That the Aura collection answers 0x5D on every model, the speed byte order, and whether the Xbox
    models need the lamp-array step.
