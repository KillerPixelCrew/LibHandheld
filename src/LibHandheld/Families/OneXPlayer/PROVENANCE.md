# ONEXPLAYER and AOKZOE

These files independently implement register transactions and wire formats. They do not include the
reference projects' implementation source. Shared Windows/CPU/input lifetime lives in
`Internal/Devices`; adding a variant changes one model row rather than duplicating that lifetime.

Primary Windows facts are pinned to
[Handheld Companion a250b19](https://github.com/Valkirie/HandheldCompanion/tree/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/OneXPlayer).
The AOKZOE inheritance is in
[Devices/AOKZOE](https://github.com/Valkirie/HandheldCompanion/tree/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/AOKZOE).
HC wins where its Windows mechanism differs from HHD. Additional selectors and protocol facts come
from
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
be restored; its journal remains unverified. An uncertain command is not automatically resubmitted.
Persistent charge, brightness, color and vibration preferences are not reset during shutdown.

The supplied sources do not establish a read operation for the original controller mapping pages.
Acquisition therefore preserves those persistent mappings, rather than overwriting them with B4
defaults that cannot be restored. The real native HID/serial event paths and ordinary XInput stream
remain available. Firmware that only produces extra buttons after rewriting mapping pages still
needs device evidence for a reversible mapping operation; it is not claimed as proven here.

The HHD-only X1 Air and X1 Pro EVA-02 inherit the X1-family control envelope where HHD supplies the
selector/protocol but no Windows power envelope. Those inherited values need hardware confirmation.
Compilation and packet fixtures are separate from live hardware acceptance; no new hardware pass is
claimed by this source port.
