# ONEXPLAYER and AOKZOE

These files independently implement register transactions and wire formats. They do not include the
reference projects' implementation source. Shared Windows/CPU/input lifetime lives in
`Internal/Devices`; adding a variant changes one model row rather than duplicating that lifetime.

Primary Windows facts come from the decompiled Handheld Companion 1.3.1.6 reference in WSGM's
_ref/HandheldCompanion, especially HandheldCompanion/Devices/OneXPlayer, Devices/AOKZOE,
Devices/IDevice.cs and Resources/Devices. Public HC a250b19 is historical corroboration only; it is
not the authoritative Windows reference. HC wins where its Windows mechanism differs from HHD.
Additional selectors and protocol facts come from
[HHD a87fb30](https://github.com/hhd-dev/hhd/tree/a87fb308f9f14d4a78ef3cb0b8515827459f8466/src/hhd/device/oxp).

| Reference paths                                                                                                    | Implementation                                                                                                                                                                                    |
| ------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IDevice.cs` One-Netbook/AOKZOE selectors; `Misc/MotherboardInfo.cs`                                               | `OneXPlayerModels`: exact BaseBoard manufacturer/product/version; G1 CPU variants remain separate.                                                                                                |
| `OneXAOKZOE.cs` takeover and indexed fan; `OneXPlayerMini.cs`, `AOKZOEA1.cs`, `OneXPlayerOneXFly.cs` fan overrides | `OneXNativeService`: ACPI F1/F2 takeover, Mini 1E, OXP2 EB; indexed 44A/44B versus ACPI 4A/4B or C4/C5; per-model minimum/maximum PWM.                                                            |
| `OneXPlayerX2.cs`, `OneXPlayerWmiEc.cs`                                                                            | `OneXWmiEc`: root/WMI `SuRwECRegInterface`, bank + offset + value packing; X2 04EB bit40 takeover, 0470 temperature.                                                                              |
| `OneXPlayerX2MiniPro.cs`, `OneXPlayer3.cs`                                                                         | X2 Mini explicitly uses indexed EC, not WMI. OXP3 inherits the verified X2 WMI mechanism. Both use HID sides 1/2/7 and 5/6.                                                                       |
| `OneXPlayerX1.cs`, `OneXPlayerX1Mini.cs`                                                                           | Firmware-gated X1 charge ceiling 04A3 and bypass 04A4 (0/1/3), serial or HID lighting as appropriate; native 64/65-byte vendor report framing. X2/OXP3 do not inherit X1 charge registers.        |
| `OneXPlayerG1.cs`, `AOKZOEA1.cs`, `OneXPlayerOneXFly.cs`, `OneXAOKZOE.cs`                                          | `OneXCommandCodec` / `OneXVendorTransport`: FF01:0001 HID v2, G1 HID v1; 18/20 RGB repetitions, brightness encoding, the 12-color palette and separate serial framing.                            |
| `Resources/Devices/OneX*.json`, `AOKZOEA1.json`                                                                    | Per-model motion matrices in the model table, including the published Apex repeated-axis quirk.                                                                                                   |
| HHD `const.py`, `base.py`, `serial.py`, `hid_v1.py`, `hid_v2.py`                                                   | HHD-only F1 EVA/OLED/L, OXP2 GA18/Pro aliases, X1 Mini Pro/Air/EVA-02 selectors; serial OEM offsets, USB/PnP COM selector, native OEM debounce/pulse semantics; A1X/Apex vendor vibration format. |

All 21 concrete HC source records for these two families are represented. G1 AMD is split by CPU
envelope; Mini A07 and HHD-only X1 variants remain independently selectable. The public consumer
selects a model or auto-detects one, then calls the same semantic TDP/fan/charge/lighting
operations. Transport handles are acquired only when a selected engine starts.

Fan/turbo originals are captured in a firmware-and-model-bound journal before the first mutation,
and restored on suspend, controller handoff and stop. Readback failure does not disable a proved
write path. If an original cannot be read, only the documented automatic/takeover-off baseline can
be restored. Accepted restores retire their journal entries without verification; failed restores
retain the original. An uncertain command is not automatically resubmitted. Persistent charge,
brightness, color and vibration preferences are not reset during shutdown.

The native OEM reader configures the applicable HC controller remap pages after the four-second
firmware readiness delay, with 50 ms between pages. X1 uses two B4 pages; X2/OXP3 add the third B4
page. X2 Mini Pro uses its own bank and rear-button keys and ends with intercept-off; Apex uses its
own B2 third page and intercept-off. A native B8 status report with byte 5 equal to 0x20 requests
the applicable configuration again after 200 ms. X1 Mini instead sends B3 [1, 5, 5], and only B8
byte 3 equal to 0xFE requests that command again. An uncertain configuration write ends the writer
and is not automatically retried by later status reports. The supplied sources do not establish
original mapping-page readback or restoration. Accepted page writes do not prove their observed
firmware state or live hardware behavior.

The HHD-only X1 Air and X1 Pro EVA-02 inherit the X1-family control envelope where HHD supplies the
selector/protocol but no Windows power envelope. Those inherited values need hardware confirmation.
Compilation and packet fixtures are separate from live hardware acceptance; no new hardware pass is
claimed by this source port.
