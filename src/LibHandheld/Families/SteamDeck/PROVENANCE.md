# Valve implementation evidence

These C# adapters are independently implemented. The reference implementations supply hardware
facts, packet fields and command identifiers; their source is not incorporated.

Primary Windows source is Handheld Companion at
[`a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`](https://github.com/Valkirie/HandheldCompanion/tree/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e):

- [`IDevice.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/IDevice.cs):
  exact manufacturer `Valve` and product `Jupiter`, `Galileo`, `Fremont` selectors. LCD/OLED share
  `SteamDeck.cs`; Fremont has its own `SteamMachine.cs`. HHD's pinned inventory contains no Deck
  hardware backend, so it supplies no replacement Windows protocol.
- [`SteamDeck.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/Valve/SteamDeck.cs),
  [`VangoghGPU.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/Valve/VangoghGPU.cs),
  [`RyzenSMU.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/Valve/RyzenSMU.cs):
  EC firmware/board combinations, LCD/OLED temperature endianness, charge-ceiling exceptions,
  documented EC fields and port `0x6C`, GPU allocated memory ranges, SMU firmware allowlists,
  slow/fast PPT and GPU/per-core CPU clock command formats. User power bounds are 4–15 W; firmware
  PPT encoding supports 3–21 W for exact restoration. Charge support is firmware dependent.
- [`NeptuneController.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Controllers/Steam/NeptuneController.cs)
  and
  [`HidEnums.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/steam-hidapi.net/Hid/HidEnums.cs):
  all physical button/axis offsets, four grips, both pad contacts, stick capacitance, trigger
  travel, accelerometer and gyro conversion. One physical `28DE:1205`, interface 2, vendor
  collection is opened; it must match 64/65-byte input and a 65-byte feature shape with a physical
  USB location.
- [`SteamMachine.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/Valve/SteamMachine.cs)
  and
  [`ValveLedService.cs`](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Processors/Valve/ValveLedService.cs):
  Fremont's 35–45 W range and 17-LED strip, on/off, 0–255 brightness scale, solid mode 3 and per-LED
  gradient mode 7. This desktop has no claimed built-in controller or Deck EC interface.

The independent
[Linux Valve HID driver](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-steam.c) and
Valve's
[SDL controller definitions](https://github.com/libsdl-org/SDL/tree/main/src/joystick/hidapi/steam)
cross-check firmware settings 9 (lizard), 71 (watchdog), 48 (IMU). The backend changes only those
settings and restores captured values; it never clears digital mappings, factory resets the
controller or overwrites Steam's custom mappings. Setting read failures retain unknown originals and
produce an unverified cleanup result. Firmware defaults enable Neptune motion; an unreadable IMU
setting is left untouched. Disconnect and release stop haptics, retire the actual read operation and
publish neutral input before retracting physical nodes.

Native haptics follow HC 1.3.3.1 `NeptuneController.SetHaptic2`: command `0xEA`, length 13, signed
intensity, weak/strong style and paired tick timestamps. The source-backed motor envelope is
repeated every 10 ms; button feedback preserves the envelope. An output failure disables only
haptics and remains latched until an explicit lifecycle or controller-management action. No
uncertain output is automatically retried. `0xEB` remains the explicit zero-output stop packet.

Fremont uses the unmodified signed upstream
[`LedsValve.p`](https://github.com/namazso/PawnIO.Modules/blob/main/LedsValve.p) module through
PawnIO. Its current getter exports capture all 17 colours, enabled state, effect and brightness
before writes. The module's `ioctl_init` changes a brightness-control bit with no getter/restorer.
It runs only for an explicit brightness request; its unknown original remains recorded, separate
from successfully restored LED colours and settings. Startup colours/brightness persistence exports
are never invoked.

Deck memory/port access requires the pre-installed InpOut x64 device before loading the
host-supplied `Resources/InpOut/inpoutx64.dll`, SHA-256
`5F27ED4D5CD58A1EE23DEEB802E09E73F3A1D884CE2135F6E827F67B171269E7`. The public
[InpOut header](https://github.com/ellysh/InpOut32/blob/master/InpOut32_dll/inpout32.h) defines
`MapPhysToLin`, `UnmapPhysicalMemory`, `IsInpOutDriverOpen` and the **void** return of
`DlPortWritePortUchar`. The
[author's distribution page](https://www.highrez.co.uk/downloads/inpout32/) documents MIT licensing
and the DLL's possible first-load driver installation; the library prevents that installation path
by checking an existing device first. Only documented EC fields and the allocated, selected GPU's
three mailbox words can be mapped. Missing mapping/driver/export access fails closed. No KX or
denied LpcIO port fallback is used. An offline review of this exact DLL and its embedded x64 driver
found that `MapPhysToLin` carries the full 64-bit physical address through `HalTranslateBusAddress`
and `ZwMapViewOfSection`; no 2 GiB address rejection or truncation was found for the zero-extended
x64 arguments used here. The DLL's void port export drops IOCTL failures, so the adapter sends the
verified three-byte `0x9C402008` port/value request through the existing driver handle and checks
its completion. Static evidence is in WSGM's local `artifacts/steamdeck-inpout-exports.log` and
`artifacts/steamdeck-inpout-driver.log`; no native target or driver was executed. The embedded x64
PE bytes reviewed hash to `CA4B055ADE3A751AAD0C3CE49C89E2A715582A5BCED9A09A46D63FA8FC307056`.

Proof limits: no Deck/Fremont hardware was accessed for this implementation. The author's legacy
InpOut mapping warning above 2 GiB was checked against the exact x64 code path, but actual Windows
driver and firmware acceptance still require hardware testing. Source/parity and fixture checks do
not prove a new live hardware pass. Original fan mode and clock minima/maxima have no documented
getters; cleanup restores readable originals and reports their remaining uncertainty without
inventing values or retrying a failed hardware write. Shared protocol and lifetime tests are source
fixtures, not USB captures or hardware acceptance.
