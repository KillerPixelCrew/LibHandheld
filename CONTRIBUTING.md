# Adding a device

The public boundary is `HandheldDevice` and `LibHandheld.Contracts`. Applications call semantic
operations such as `SetTdpAsync`; model profiles and private family services select the native
implementation. Keep model checks, packets and register addresses behind that boundary.

1. Identify the exact Windows board, system product, processor variant, firmware and internal USB
   interfaces. HC is the primary Windows reference, HHD the cross-check and OpenGamepadUI tertiary.
   Record the pinned source and any disagreement in the family's `PROVENANCE.md`.
2. Find a family with the same protocol. Add a model profile there, including exact selectors,
   bounds, coordinate transforms and required Windows access. Shared CPU, EC fan, controller, sensor
   and OEM-key services are under `Internal/Devices`, `Internal/Transports` and `Internal/Windows`.
   GPD and OneXPlayer show profiles sharing lifecycle and native feature services.
3. Add a private native service only for a protocol the shared services cannot implement. A new
   protocol family registers once in `Internal/DeviceFamilies.cs`; variants stay in its profile
   table. Implement actual commands, observation, input and release behavior. Consumer APIs and WSGM
   UI should not gain switches on model names.
4. Declare capabilities with semantic roles, current bounds and logical instances. Keep unreadable
   observations unknown, preserve accepted writes, and retain uncertain outcomes without retries.
   Capture original temporary state before changing it where the protocol permits; preserve durable
   user settings. Detection must never open mutable transports.
5. Add source fixtures for matching, packet encoding, input decoding and restoration. Compile and
   format the library. Update implementation coverage with concrete paths and proof limits; a
   profile declaration alone does not establish support. Follow the maintainer's manual-first test
   policy.

Physical identity belongs to the built-in device. A CPU-only fallback sets `HasController = false`
and cannot claim an arbitrary external controller. Explicit selection is not an override of native
identity or firmware checks.

All devices use the same observer and lifecycle contracts. The application owns profiles, AutoTDP,
virtual targets, hiding physical controllers and OEM action assignments. Hardware acceptance remains
separate from successful compilation and source fixtures.
