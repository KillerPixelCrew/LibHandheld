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

This contribution supplies exact detection, standard controller/rumble routing, OEM button decoding,
reconnect/release handling and stock gamepad glyphs. Existing Intel processor settings remain owned
by the shared processor service and declare its IntelKx dependency. No model-specific TDP envelope,
fan, charging, lighting or motion capability is declared in this initial input contribution.

Predator Sense 1.0.1563.0 was supplied separately by the maintainer for offline protocol research.
The follow-up [power and fan protocol research](POWER_FAN_PROTOCOL.md) records the separate EC HID
power path, WMI fan commands, field layouts and remaining recovery/acceptance work. Its proprietary
binaries and decompiled source are not included in the library. Protocol findings are research
inputs; motion units/orientation, native controls and restoration need their own reviewed
implementation and measured acceptance.

HardwareVerified remains false. Published reports are source fixtures, not a Windows hardware pass.
Confirm identity/collection shapes, physical pad and rumble, Guide/QAM/M1/M2, held-button
disconnect, integration/controller-management transitions and suspend/resume on the maintainer's
unit.
