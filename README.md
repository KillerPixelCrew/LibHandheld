# LibHandheld

Windows-first .NET hardware library, directly consumed by WSGM. The initial implementation contains
five MSI Claw and four ASUS ROG Ally definitions, migrated from the existing MIT WSGM engines. It
provides native identification, power, fans, charging, lighting, controller input, motion, haptics,
OEM controls, glyphs and recovery without a WSGM SDK or device-package loader.

The complete target remains every device supported by Handheld Companion (HC) or Handheld Daemon
(HHD). The [inventory](inventory/README.md) contains 201 HC/HHD source records, not 201 unique
physical devices. Nine seed definitions are implemented; the remaining families and Windows
transports still require implementation. An inventory row or contribution scaffold is not support.

## Use and ownership

The public [API](API.md) is `HandheldDevice` and typed `LibHandheld.Contracts`, with a borrowed
`IHandheldObserver`. Detection matches supplied identity and does not open hardware. Start acquires
native services; the caller owns lifecycle sequencing, generation, state-directory selection and
terminal restoration. Cancellation does not establish that a native operation stopped.

The project targets `net10.0-windows10.0.19041.0`, x64, and uses System.Management for Windows
hardware access. Internal helpers and family engines remain private; the consuming application owns
profiles, AutoTDP policy, virtual-controller routing, HidHide, Steam integration and UI. Vendor GPU
mechanics belong to [LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract).
Generic Windows controls remain in
[WindowsDeviceControl](https://github.com/KillerPixelCrew/windows-device-control).

## Evidence and contributions

HC is the primary Windows source, HHD secondary and OpenGamepadUI tertiary. Conflicting observations
and pinned versions remain visible in the inventory. Linux-only mechanisms require concrete Windows
implementations. Original migrated family behavior, glyph bytes and notices are preserved; source
migration and compilation do not constitute a new hardware pass.

For a contribution, identify the exact model, board, controller interfaces and firmware version.
Document protocol provenance and variant predicates, implement native services, and add parser or
encoder fixtures. Record source support separately from attended hardware observations. Unknown
input decoders remain explicitly unimplemented research. Shared helpers should serve concrete
protocols rather than create another plugin framework.

- [API](API.md): contracts, operation results and native lifetimes.
- [Plan](PLAN.md): applied extraction and remaining full-device target.
- [Inventory guide](inventory/README.md): snapshots, evidence and source counts.
- [Combined records](inventory/devices.json) and [device table](inventory/devices.csv): research
  target.
- `src/LibHandheld/Families`: native implementations and preserved provenance.
- `tests`: migrated Claw/Ally regression sources and fixtures.

WSGM tracks this repository as `external/libhandheld`. Commit and push library changes before
recording its gitlink in WSGM. Both repositories remain private during testing; automatic CI is off
for `chore/device-integration-rework`. New automated tests and hardware acceptance must be reported
from actual runs, separately from compilation and source evidence.
