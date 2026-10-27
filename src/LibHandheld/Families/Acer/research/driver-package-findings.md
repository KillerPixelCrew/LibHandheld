# Atlas 8 ancillary driver package findings

Offline inspection on 2026-10-27 using execution-free ZIP/INF parsing and bounded binary string
inventories. No vendor program was run, no package installed, and no hardware or service opened.
These package-declaration findings have archive/member/line references; they do not claim REA native
control-flow evidence.

The private `inventory.json` records source archives, SHA-256 digests, member names and uncompressed
sizes. Original text members and `binary-string-leads.json` remain under ignored
`artifacts/acer-atlas8-rea-20261027/drivers/` in the WSGM integration checkout. No vendor binaries,
original INF files or decompiled source are redistributed here. Paths and line numbers below refer
to members of the named supplied ZIPs.

## Source archive identities

| Supplied archive                               | SHA-256                                                            |
| ---------------------------------------------- | ------------------------------------------------------------------ |
| `APP Base driver_Acer_1.0.0.6_W11x64_A.zip`    | `2fd21d36541216151ff7e7bfc552cd15f259946df8f6e294182ff233357fe958` |
| `DES Driver_Acer_1.0.0.3020_W11x64_A.zip`      | `8eb2b9c3d195d697534c7fe573696290d588cc4f001c26caff98877cf043f4fc` |
| `Intel ISH_Intel_5.8.62.0_W11x64_A.zip`        | `80503fa55354aa3f4f7b1ea6b4fdcb9d24fdbd622d6ed3e2dfb8369147490a37` |
| `HID event filter_Intel_2.2.2.17_W11x64_A.zip` | `a1404b6bd4b6f93c3f562519a6543ccc820c796e6162a923983f9a45413d5a0a` |
| `Intel PPM_Intel_1.0.0.222_W11x64_A.zip`       | `1b9616d8527b528743bb54c77a0b835b636e700953df7ee894a9295ff78d0960` |
| `Intel PMT_Intel_7.3.10.3_W11x64_A.zip`        | `4442cabc5e7ac4a5514f61f9a9d1d9939aa112a95251d11b7d6feb04e6bb185c` |

## Findings

### Intel sensor hub has a model-specific firmware extension

Archive: `Intel ISH_Intel_5.8.62.0_W11x64_A.zip`.

- `Intel ISH_Intel_5.8.62.0_W11x64/IshHeciExtensionTemplate/x64/IshHeciExtensionTemplate.inf:68`
  binds `{95511210-D1F0-4091-B373-46FDCC5329F7}\ISH_HECI&SUBSYS_21491025`.
- Its lines 74, 85, 93, 106 and 123 select the PTL firmware
  `FWImage/0004/BU4_PTL_ishC_SI_5.8.1.7783_16.bin`. This is a concrete subsystem-specific extension,
  despite the INF's Template filename.
- Base `IshHeci/x64/IshHeci.inf:71-72` binds `...\ISH_HECI_LEGACY` and `...\ISH_HECI`, with
  `IshHeci.sys` and `OffloadItemService.exe`. Its diagnostic/interface declaration at line 199 is
  `GUID_DEVINTERFACE_ISHHECI = {aa1dcd67-308f-4daa-a2b9-2782d4703c5d}`. Treat that as a HECI
  interface, not a Windows gyro interface.
- `IshOed/x64/IshOed.inf:68-69` supports PTL PCI IDs `PCI\VEN_8086&DEV_E345` and
  `PCI\VEN_8086&DEV_E445` through `IshOed.sys`.
- `IshHidBus/x64/IshHidBus.inf:48` binds `{45EA05A3-BF9D-4FF5-AA70-91A30F0739C7}\HECI_BUS` through
  `IshHidBus.sys`.
- `IshHidMini/x64/IshHidMini.inf:53` binds `{DEA5AE2A-D1FD-438A-A091-CBD484788436}\ISH_HID_MINI`; it
  installs `IshHidMini.sys` with inbox `mshidkmdf` as UpperFilters (line 79), enabling selective
  suspend (line 83).
- `IshIpcm/x64/IshIpcm.inf:46` separately binds `{45EA05A3-BF9D-4FF5-AA70-91A30F0739C7}\IPCM` and
  describes it as Intel ISS Power Metering Device.

This creates a concrete second motion/sensor-discovery lead alongside the already documented
controller `0502:F901` motion-upload protocol. These INFs do not identify a physical gyro sensor,
HID sensor usage/report descriptor, units, axis mapping or sample clock. They do not prove that the
Intel hub owns the gamepad gyro or that all packaged IDs are installed on the maintainer's unit. The
package contains radar offload DLLs too, reinforcing that it is a shared ISS distribution rather
than a complete gyro capability declaration.

### Acer base driver and device-enabling service are separate from PredatorSense hardware services

Archive: `APP Base driver_Acer_1.0.0.6_W11x64_A.zip`, member
`APP Base driver_Acer_1.0.0.6_W11x64/AcerApplicationBaseDriver.inf`.

- Lines 32-33 bind `ACPI\VEN_1025&DEV_165F` and `ACPI\VEN_1025&DEV_1783`.
- Lines 44-51 install kernel service `AcerApplicationBaseDriver_Device`, demand start, binary
  `AcerApplicationBaseDriver.sys`, System setup class.
- No public custom device-interface GUID or sensor-class declaration is present in this INF. A
  bounded static string inventory of the driver likewise did not establish a user-mode protocol.
  This is not evidence that no interface exists in native code.

Archive: `DES Driver_Acer_1.0.0.3020_W11x64_A.zip`.

- `DES Driver_Acer_1.0.0.3020_W11x64/ext/AcerDeviceEnablingServiceExtension.inf:53,86` explicitly
  includes `ACPI\VEN_1025&DEV_1783&SUBSYS_40031025`, commented `Gaming Console`. It also matches
  many Acer laptop/desktop categories; this is not exact Atlas detection by itself.
- The extension creates software component `SWC\SWComp_ADES` (`ComponentIDs=SWComp_ADES`).
- `swc/AcerDeviceEnablingServiceComponent.inf:21,45,56` binds that component and installs auto-start
  own-process service `AcerDeviceEnablingServiceV2`, binary `ADESv2Svc.exe`.
- That service has separate ownership from the previously documented PredatorSense
  `AASHHSvc`/AcerAgentService/AcerHardwareService/AcerLightingServiceHH stack.
- The ZIP label and software-component INF version are 1.0.0.3020, while the extension INF is
  independently versioned 1.0.0.3016. This is an observed package version difference, not proof of a
  broken installation.

### Intel HID events and platform power/telemetry packages

- `HID event filter_Intel_2.2.2.17_W11x64_A.zip` ->
  `HID event filter_Intel_2.2.2.17_W11x64/HidEventFilter.inf:83-84` explicitly marks `ACPI\INTC10CC`
  as PTL. It installs `HidEventFilter.sys`, HIDClass. This is a concrete match for the Intel
  HID-events device in the earlier public Linux inventory, not evidence that Acer M1/M2/QAM run
  through that path.
- `Intel PPM_Intel_1.0.0.222_W11x64_A.zip` ->
  `Intel PPM_Intel_1.0.0.222_W11x64/ipfppm_sw.inf:100-101,108-109` supports software components
  `SWC\VID8086_PPM_1008_1.0` and `SWC\VID8086_PPM_CORP_1008_1.0`. It selects PTL consumer/corporate
  provisioning PPKGs by OS build; lines 187 and 205 select consumer files for Win11 24H2/23H2. The
  selected service sections contain null `AddService`, so this is provisioning data, not an Acer
  fan/TDP runtime controller. Contents of the provisioning packages were not decoded.
- `Intel PMT_Intel_7.3.10.3_W11x64_A.zip` -> `Intel PMT_Intel_7.3.10.3_W11x64/intcpmt.inf:58-59`
  names PTL PCI IDs `8086:680D` (S) and `8086:B07D` (U/P/H). Lines 74, 97 and 99 install boot-start
  kernel service `intcpmt` / `intcpmt.sys`; line 82 sets `PmtDeviceType=1` (PCIe). This is a
  platform telemetry lead, not an established source for the Atlas's fan RPM or a substitute for the
  already decoded Acer control protocols.

## Scope and proof limits

All six archive inventories were read completely. INF/configuration observations are exact package
declarations. Bounded APP Base/DES strings are only search leads; no native control-flow claim is
made from them. No driver was disassembled in this pass. Actual PnP topology, sensor collections,
permissions, sample scale/cadence, per-unit driver binding and service coexistence remain live
evidence requirements. This research adds no runtime device support.
