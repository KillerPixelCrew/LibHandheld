# LibHandheld

A planned Windows-first .NET library for handheld hardware support. The goal is to support every
device supported by Handheld Companion (HC) and Handheld Daemon (HHD), with device additions and
corrections contributed through pull requests.

The repository currently contains the device inventory and migration plan. Hardware backends have
not been migrated into this library yet.

## Source priority

1. **Handheld Companion** is the primary source for device facts because it implements Windows
   hardware access.
2. **Handheld Daemon** supplies additional device coverage and secondary protocol facts.
3. **OpenGamepadUI**, including its actual hardware-service dependencies, supplies tertiary facts.

When facts disagree, HC takes priority over HHD, and HHD over OpenGamepadUI. The inventory keeps the
conflicting observations and their source versions. A Linux-only path is recorded as requiring a
Windows implementation. Missing facts remain unknown.

LibHandheld will independently implement device behavior and protocols. Upstream reference code is
used for research and is excluded from this repository's tracked files.

## Inventory

- [Inventory guide](inventory/README.md): scope, evidence rules, source snapshots and coverage.
- [Combined device inventory](inventory/devices.json): the HC/HHD target set with tertiary evidence.
- [Device table](inventory/devices.csv): a spreadsheet-friendly view of the same target set.
- [Source inventories](inventory/sources/): complete source-specific records and readable notes.
- [Migration plan](PLAN.md): the path from the current WSGM device packages to this library.

Concrete model identifiers, aliases, board/CPU variants and generic matching ranges are kept
distinct. A source-supported model is a migration target, not a claim that LibHandheld already
implements or has tested it.

## Boundary

LibHandheld will own device identification, hardware transports, device-specific power controls,
fans, charging, lighting, OEM buttons, physical controller input, motion, haptics and restoration of
temporary hardware changes.

The consuming application owns profiles, AutoTDP policy, virtual-controller routing, HidHide,
Steam integration, user preferences and UI. Vendor GPU driver controls belong in the separate
[LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract) library. Generic
Windows controls remain in [WindowsDeviceControl](https://github.com/KillerPixelCrew/windows-device-control).

WSGM will consume this library directly. Its Device SDK and device-package loading are to be
retired after the migration; the common Plugin SDK remains for independent integrations such as IR.

## Contributions

For a device contribution, identify the exact model, board, controller identifiers and relevant
firmware version. Document the source of protocol facts, add fixtures or tests for parsing and
encoding, and record hardware observations separately from source-derived support. Shared helpers
should serve concrete devices rather than introduce a general plugin framework.

Correct the inventory before implementing a device whose identity or protocol is ambiguous.

## WSGM checkout

WSGM tracks this repository as `external/libhandheld`. Work in that submodule, commit and push its
changes here, then commit the updated gitlink in WSGM.
