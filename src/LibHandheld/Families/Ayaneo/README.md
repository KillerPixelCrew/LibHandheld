# AYANEO and SuiPlay

`AyaneoModels` owns the exact manufacturer, product and processor selectors and immutable model
facts. Add a variant as one model row when it uses an existing codec. `ProfiledDeviceEngine` owns
power, XInput input, motion, OEM events, haptics, suspend/resume, controller enablement and hotplug.
A constructor or identity match never opens a mutable handle.

`AyaneoNativeService` supplies the CEc and CEii RGB and charge-bypass codecs and FLIP DS secondary
display brightness. Its indexed fan addresses are model data consumed by the shared fan service. CEc
uses the ACPI EC for RGB/bypass and indexed EC for the fan. CEii uses indexed `D1xx` EC registers
for RGB/bypass and `18xx` for fan duty. AIR 2 uses duty `1809`, control `02F1`, and a scale of `B8`.
The original lighting ownership register is journalled before acquisition, bound to the current
firmware identity and restored on release. Successful lighting/charge choices are retained; a
partial lighting write clears automatic resume reapplication.

`AyaneoSuperJoy` is the separate `1C4F:0002`, `FF00:0001`, 65-byte HID collection on AYANEO 3 and
NEXT 2. It performs RGB commands and reads modular-controller identity. The normal gamepad data
still comes from the physical XInput collection, as in both reference implementations. HC's
alternate DirectInput mode uses the shared descriptor-driven HID input source with its exact
X/Y/Z/Rz stick, Rx/Ry trigger and X/A/B/Y button order. That alternate mode does not advertise an
unsupported rumble implementation. Its native mapping action selects the custom profile, writes the
33 mapping slots and saves exactly once. Rear keys are mapped to F19/F20/F17/F18, matching HC's safe
Windows OEM key representation. The device exposes no source-supported readback of the original row
table, so this is an explicit, durable user action rather than a fabricated temporary takeover. A
response timeout never repeats a submitted command. AYANEO 3 additionally implements active-low EC
module presence (`2F`), controller power (`2D`, `FE`/`FF`), and each side's native eject command.
Eject waits for the latch operation and then powers down the controller. Removing and reinserting
both modules reacquires power once; uncertain writes are not retried by observation. Controller
power ownership is captured in the same BIOS-bound journal and is released with controller
integration.

Battery percentage thresholds belong to the application. The hardware mechanism is bypass charging,
not a programmable firmware ceiling or a separate charge-protection register. The library exposes
that actual mechanism; applications may switch it when their user-owned threshold is reached.

AYANEO 2021 and NEXT families have no source-supported CEc/CEii fan, RGB or charging controls. They
use the shared AMD power, Windows motion and controller/OEM/haptic paths without borrowed EC writes.
KUN includes the older firmware's left-mouse plus XButton2 Guide chord as well as the newer F-key
chords. AYN is a different vendor and is never matched to these EC codecs.

## Sources and coverage

Primary Windows source: the decompiled Handheld Companion 1.3.1.6 reference in WSGM's
_ref/HandheldCompanion, including Devices/AYANEO, Devices/SuiPlay, Devices/IDevice.cs and
Resources/Devices. Public HC a250b19 is historical corroboration only. Exact source records remain
the evidence for each model and wire value. Secondary:
[HHD a87fb30 AYANEO protocol](https://github.com/hhd-dev/hhd/tree/a87fb308f9f14d4a78ef3cb0b8515827459f8466/src/hhd/device/ayaneo)
and
[exact generic aliases](https://github.com/hhd-dev/hhd/blob/a87fb308f9f14d4a78ef3cb0b8515827459f8466/src/hhd/device/generic/const.py).
HHD's module-power sysfs backend is independently reimplemented from the underlying
[Linux AYANEO EC driver](https://github.com/torvalds/linux/blob/2643187ccb8628144246ee9d44da5e3ac428f9c3/drivers/platform/x86/ayaneo-ec.c#L34),
whose module-power quirk applies only to AYANEO 3. NEXT 2 uses SuperJoy lighting without borrowing
AYANEO 3's EC module-power registers. This is an independent implementation of the recorded
contracts and firmware interactions.

| HC factory classes                                                                | Library profiles / shared codec                                                        |
| --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| AYANEO2021, AYANEO2021Pro                                                         | 2021, 2021-pro; no AYANEO EC controls                                                  |
| AYANEONEXT, AYANEONEXTLite, AYANEONEXTLite4500U                                   | next, next-lite, next-lite-4500u; no AYANEO EC controls                                |
| AYANEOAIR, AYANEOAIRLite, AYANEOAIRPro                                            | air, air-lite, air-pro; CEc and AIR matrix/chords                                      |
| AYANEOAIR1S, AYANEOAIRPro1S                                                       | air-1s, air-pro-1s; CEc, AIR 1S matrix/chords                                          |
| AYANEOAIR2                                                                        | air-2; CEc, AIR 2 fan registers and scale                                              |
| AYANEO2, AYANEO2S                                                                 | 2, 2s; CEc with AYANEO 2 matrix                                                        |
| AYANEOAIRPlusAMD, AYANEOAIRPlusAMDMendocino, AYANEOAIRPlusIntel, AYANEOAIRPlusNxx | air-plus-amd, air-plus-mendocino, air-plus-intel, air-plus-nxx; CEii CPU/duty variants |
| AYANEOSlide                                                                       | slide; CEii with SLIDE matrix                                                          |
| AYANEOKUN                                                                         | kun; CEc RGB channel permutations and old/new OEM paths                                |
| AYANEOFlipKB, AYANEOFlipDS                                                        | flip-kb, flip-ds; CEc, DS brightness; no unsupported DS RGB                            |
| AYANEOFlip1SKB, AYANEOFlip1SDS, AYANEOFlip11                                      | flip-1s-kb, flip-1s-ds, flip-11; child motion/chord overrides                          |
| AYANEO3                                                                           | 3-8840u, 3-hx370; CPU range split, SuperJoy                                            |
| AYANEONEXT2                                                                       | next-2; NEXT 2 motion, SuperJoy                                                        |
| SuiPlay0X1                                                                        | suiplay-0x1; CEc and current F-key chords                                              |

All 27 primary factory classes have a concrete implementation. HHD's `AIR Plus` alias is selected
only with an exact known CPU brand or an explicit, bounded CPU SKU token; an ambiguous CPU never
selects a larger range by default. Source fixtures exercise identity, fan scales, child motion maps,
EC codecs, ownership restoration and HID frames. Compilation and fixtures do not establish a new
hardware acceptance pass.
