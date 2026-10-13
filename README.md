# LibHandheld

Windows-first .NET hardware library, directly consumed by WSGM. Native families cover MSI Claw, ASUS
ROG Ally, Lenovo Legion Go, GPD, AYANEO, OneXPlayer/AOKZOE, Valve, Zotac and the documented
remaining exact Windows targets. The original Claw and Ally engines were migrated from MIT WSGM
sources. The library provides native identification, power, fans, charging, lighting, controller
input, motion, haptics, OEM controls, glyphs and recovery without a WSGM SDK or device-package
loader.

Device information and protocol research were collected from public and open sources such as
[HHD](https://github.com/hhd-dev/hhd),
[HandheldCompanion](https://github.com/Valkirie/HandheldCompanion),
[InputPlumber](https://github.com/ShadowBlip/InputPlumber) and
[OpenGamepadUI](https://github.com/ShadowBlip/OpenGamepadUI). Source attribution and resource
licenses remain beside the implementations. The library contains 112 exact definitions and 106 glyph
profiles. **Only MSI Claw 8 A2VM and ROG Xbox Ally X are hardware-tested.**

## Documentation

Start with the [documentation index](docs/README.md):

- [Getting started](docs/getting-started.md): build, reference, detect, acquire and stop.
- [Devices and dependencies](docs/devices.md): identity, native providers and availability.
- [Lifetime and recovery](docs/lifecycle.md): ownership, suspend/resume and restoration.
- [Controls](docs/controls.md): semantic setters, values, observations and outcomes.
- [Input, haptics and glyphs](docs/input.md): observer callbacks, units, OEM events and resources.
- [Troubleshooting](docs/troubleshooting.md): diagnosis and explicit recovery.
- [Complete API reference](docs/reference.md): public types, members and enum values.

## Use and ownership

The public [API](API.md) is `HandheldDevice` and typed `LibHandheld.Contracts`, with a borrowed
`IHandheldObserver`. Detection matches supplied identity and does not open hardware. Start acquires
native services; one device owner serializes operations while the caller owns state-directory
selection and terminal restoration. Cancellation does not establish that a native operation stopped.

Match the supplied identity with `Detect(identity)`, create the result with
`Create(definition, identity, stateDirectory, diagnostics)`, then use common operations such as
`SetTdpAsync`, fan, charging and lighting setters. A family registry and shared Windows services
route those operations to the appropriate native implementation. Variants sharing a protocol
normally require one model profile. See [adding a device](CONTRIBUTING.md).

The project targets `net10.0-windows10.0.19041.0`, x64, and uses System.Management for Windows
hardware access. Internal helpers and family engines remain private; the consuming application owns
profiles, AutoTDP policy, virtual-controller routing, HidHide, Steam integration and UI. Vendor GPU
mechanics belong to [LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract).
Generic Windows controls remain in
[WindowsDeviceControl](https://github.com/KillerPixelCrew/windows-device-control).

## Evidence and contributions

HandheldCompanion is the primary Windows reference, HHD secondary and OpenGamepadUI tertiary. Family
provenance preserves source attribution and disagreements. Linux-only mechanisms require concrete
Windows implementations. Original migrated family behavior, glyph bytes and notices are preserved;
source migration and compilation do not constitute a new hardware pass.

For a contribution, identify the exact model, board, controller interfaces and firmware version.
Document protocol provenance and variant predicates, implement native services, and add parser or
encoder fixtures. Record source support separately from attended hardware observations. Unknown
input decoders remain explicitly unimplemented research. Shared helpers should serve concrete
protocols rather than create another abstraction layer.

- [API](API.md): contracts, operation results and native lifetimes.
- [Contributing](CONTRIBUTING.md): model profiles, protocol services and source requirements.
- [Documentation](docs/README.md): public integration guides and API reference.
- `src/LibHandheld/Families`: native implementations and preserved provenance.
- `tests`: migrated Claw/Ally regression sources and fixtures.

WSGM tracks this repository as `external/libhandheld`. Commit and push library changes before
recording its gitlink in WSGM. LibHandheld and LibGPUDriverInteract are public; automatic CI remains
off until explicitly enabled. New automated tests and hardware acceptance must be reported from
actual runs, separately from compilation and source evidence.
