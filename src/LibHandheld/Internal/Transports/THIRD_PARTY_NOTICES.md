# LibHandheld transport dependencies

LibHandheld's transport implementation is independently written. Hardware register addresses,
command IDs and transaction formats were checked against Handheld Companion's Windows implementation
and the ACPI embedded-controller specification; its source code is not incorporated.

- LibreHardwareMonitorLib 0.9.6 is a separate dependency under the Mozilla Public License 2.0.
  Copyright LibreHardwareMonitor and contributors. Its unmodified signed `LpcIO`, `LpcACPIEC` and
  `IntelMSR` PawnIO modules supply the permitted kernel operations. Source and licence:
  <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>.
- `RyzenSMU.bin` is the unmodified signed module from namazso's PawnIO.Modules 0.2.11 release.
  SHA-256: `301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450`. It is obtained from
  the upstream release, not compiled from Handheld Companion's source. Upstream module terms and
  signed release: <https://github.com/namazso/PawnIO.Modules>. This binary is not covered by
  LibHandheld's MIT licence.
- `LedsValve.bin` is the unmodified signed module from upstream 0.2.11, SHA-256
  `1C1809281AE6F3E3C5B362F1536C0E6706013E6E33BFBA66CAD74636E61436D6`. The upstream `RyzenSMU` and
  `LedsValve` modules use LGPL-2.1-or-later; the included `LGPL-2.1.txt` applies independently of
  the library's MIT implementation. Corresponding module sources:
  <https://github.com/namazso/PawnIO.Modules/tree/52a7e536dff3e53c96917a28caac5e0fa6510696>.
- PawnIO is an independently installed dependency. The host supplies the verified installer;
  LibHandheld never installs, upgrades or removes its driver. PawnIO verifies module signatures;
  unrestricted loading is never requested. <https://pawnio.eu>.
- Intel package power limits and OC-mailbox settings use only the signed `IntelMSR` module above.
  No unsigned register helper is used or redistributed.

Missing dependencies and rejected native calls are reported as errors.

Raw ACPI embedded-controller transactions remain inherently subject to Windows and firmware access;
the shared `Global\Access_EC` mutex coordinates cooperating applications, not the ACPI kernel
driver. Family adapters must use their vendor's WMI/HID protocol wherever the primary reference
supports it and must never scan the controller or probe another family's registers.
