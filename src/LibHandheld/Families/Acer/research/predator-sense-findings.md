# Acer Predator Sense offline findings

Source package:
`C:\Users\d.hirt\Downloads\Predator Sense_Acer_1.0.1563.0_W11x64_A\PS_HH_RC7_1.0.1563.0`. Inspected
2026-10-27. No vendor executable was run, no installation performed and no hardware accessed.

## Binary identities and provenance

SHA256:

- `drivers/AcerAgentServiceHH/swc/AcerHardwareService.exe`:
  `3AAD1B07EF5B22D11505FD9193873EBD5ACB87CF29D68556EF83DEBFEB7FCE36`
- `drivers/AcerAgentServiceHH/swc/AcerHIDStickController.dll`:
  `3836B8A5C200308EB9A47210BCAD89AFD1051DC3EBF51C83AFC32448E80E36EE`
- `UWP/Desktop/7bd2a3c9c08b457ea173692ad3e17700.msixbundle` ->
  `Handheld.Desktop.Package_1.0.1563.0_x64.msix` -> `Handheld.Desktop/AcerGamePad.Core.dll`:
  `41392F81A12D84498BCB1C1CEC7C6D81A23718252B8473B96BFB6E7A2253370D`

Managed controller assembly decompiled using installed ilspycmd 10.1.1 into `gamepad-decompiled/`.
Native service disassembled using objdump into `AcerHardwareService-disassembly.txt`; ASCII/UTF16
string inventories also retained. These are private research artifacts, not source to copy wholesale
into a permissive library. Acer HID DLL identifies Acer Incorporated, all rights reserved. Bundled
OpenRGB and License.txt do not establish a permissive license for the Acer controller assembly.
Reimplement observed protocol facts with independent code and record provenance.

## Controller transport and buttons

`gamepad-decompiled/HJC.AcerGamePad.Core.Services/DeviceListener.cs` explicitly lists VID 0x0502 and
PIDs F900/F909 (DG_3315), F901/F90A (DG_3315T), F310/F319 (DG_3313), F311/F31A (DG_3313T), plus
VID0489 variants. This supports the existence of related firmware, not admission of those other
devices into an exact Atlas definition.

Vendor collection selected by output report ID 0x0F. Gamepad collection identified by output report
ID 0x05. The managed listener does not hardcode a vendor usage pair, so it does not independently
confirm InputPlumber's usage tuple. `Models/DeviceInfo.cs` allocates max input report length from
descriptor; command replies are accepted only at length64 by DeviceProxyService.ReceiveData. This
does not establish length64 for OEM input reports.

Native `AcerHardwareService.exe` callback at VA 0x140014850 independently recognizes report IDs 0x9A
and 0x9E:

- 0x9A requires length >=14 (cmp r9d,0xE). Reads little endian words at supplied data pointer +1,
  +3, +5. Debug format is `ReportID: %02X M:%X K1:%d K2:%d`. If third word nonzero it releases
  mapped action. Otherwise it passes bit1 and bit2 of first word plus second word to internal
  handler at 0x1400056D0. This is consistent with modifier/macro signaling; exact M1/M2 ownership
  should remain tied to capture evidence.
- 0x9E requires length >=5. Reads LE words at data pointer +1 and +3. Requires first word zero, then
  dispatches on low byte of second word values1..5. Nearby strings include RT Trigger On/Off, LT
  Trigger On/Off, and Handheld Widget Key. Therefore **0x9E is not solely a Guide report**. Exact
  Guide bit/button mapping is capture-dependent; do not treat every 0x9E payload as Guide.
- This bounded inspection did not independently locate report0x37/QAM semantics. InputPlumber
  capture remains the separate evidence for that mapping.

Native callback buffer conventions must be confirmed before translating pointer offsets to full raw
HID buffer offsets. Disassembly alone does not establish whether wrapper has already removed a
report ID.

## Motion packet contract

Exact pointers: `gamepad-decompiled/HJC.AcerGamePad.Core.Services/DeviceProxyService.cs` methods
SetMotionStatus and ReceiveData; `HJC.AcerGamePad.Core.Architecture.Models/DeviceMessage.cs`;
`HJC.AcerGamePad.Core.Models.CommandModels/CommandType.cs`.

DeviceMessage builds output bytes `0F 00 00 3C command payload...`; writes are HidStream.Write, not
HID feature reports.

- Enable motion upload: `0F 00 00 3C 2F 01`; disable: `0F 00 00 3C 2F 00`. Descriptor/padding
  requirements must be confirmed live before runtime use.
- Motion reply requires length64 and command byte[4]=0x30.
- Gyroscope: signed little endian int16 at offsets5/7/9; accelerometer at11/13/15.
- Vendor adds32768 to signed values for its display model. This is a display offset, not a physical
  scale.
- Units, sensor full scale, sample timing/counter, axis basis, stationary bias and exact report
  framing are not established. No production physical-unit motion capability should be claimed from
  this finding alone.

## Other controller commands

Same prefix `0F 00 00 3C`; command enum and DeviceProxyService:

- 0x26 read gamepad mode; reply0x27 has payload from byte5: gamepad mode, M key function, vibration
  mode, stick mode, quick-trigger bitfield (bits0/1).
- 0x24 switch mode with payload mode and M key function. GamepadMode enum: Offline0, XInput1,
  DInput2, Special3, Desktop4, BIOS5, TESTING6. MKeysFunction: Macro0, Combination1, Disabled2. Mode
  switching may change topology; not exercised.
- 0xFD calibration control; reply0xFE byte5==1 calibration active, byte6 bits0..4 LT/RT/LS/RS/motion
  completion. Do not invoke automatically.
- 0xE0 RGB control payload play flag and frame index; 0x09 sync RGB; RGB status reply0x0A has nine
  RGB triples from byte5.
- 0x22 sync to ROM, 0x23 restore from ROM, profile writes modify onboard configuration. These are
  destructive/persistent configuration commands and unsuitable for automatic discovery.
- 0x2C feature enable state uses inverted flag (enabled ->0); 0x2D device disable state uses direct
  flag. Do not send as part of basic input acquisition.

## EC, power, fan and lighting

Native service has `acer::KYD100::EcHID` methods GetDataVersion, GetSystemStatus, GetOCProfileCap,
Get/SetSystemUsageMode, Get/SetOCProfile, GetOCProfileLimit, GetSystemHealthInformation and
GetDeviceStatus. Global serialization mutex string `Global\ECHIDFeatureVID_%04X&PID_%04X`. HID
feature transport exists independently from controller output transport. Exact report IDs, device
usage tuple, command packing, PL1/PL2 units and acknowledged return codes were not fully decoded in
this bounded pass.

Agent-side string explicitly logs `Send command mode: %d PL1: %d PL2: %d error: %d` for
HIDSetOperationMode. This is evidence of OEM adjustable power fields, not their units or accepted
ranges.

WMI namespace `ROOT\WMI`; classes `APGeAction` (uiInput/uiOutput; GetFunction/SetFunction) and
`AcerGamingFunction` (gmInput/gmOutput; Get/SetGamingProfile, GetGamingSysInfo,
Get/SetGamingFanBehavior, Get/SetGamingFanSpeed, Get/SetGamingFanTable, Get/SetGamingMiscSetting).
Names confirmed by static strings; packing/selector values remain unknown. Presence in binary does
not prove use on Atlas hardware because same service has legacy WMI and newer EC HID paths.

`RGBDevice.ini` declares EC\ACPI1 -> AcerHIDStickController and EC\ACPI2 ->
AcerHIDLightbarController. Acer HID lighting DLL uses descriptor-discovered field sizes and feature
reports (GetLightingDeviceAttributeReport/GetLightingDeviceBaseInfo/SetLightingDeviceUpdateReport).
This is another transport distinct from controller RGB profile commands. Do not infer their
equivalence.

`PredatorService.inf` installs AASHHSvc (AcerCentralService.exe) and AcerLightingServiceHH
(AcerLightingService.exe), software component SWC\SWComp_AcerHHAgentServiceDriver, driver
version1.3.3000.18 dated2026-08-05. Vendor runtime conflict/coexistence has not been tested.

## Acceptance limits

All findings are offline evidence. Exact machine identity, collection descriptors, full raw report
offsets, button release/rollover, controller disconnect/resume, OEM coexistence, physical motion
scaling and any hardware writes remain untested. No runtime control implementation or hardware pass
is implied.
