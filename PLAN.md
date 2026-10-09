# LibHandheld migration plan

## Accepted decisions

- Windows-first .NET library.
- Full target: every device supported by HC or HHD; OpenGamepadUI is tertiary evidence.
- Conflicting facts use HC > HHD > OpenGamepadUI.
- Independent implementation of hardware mechanics; reference source is research input.
- Device support moves out of separately installed WSGM device packages.
- Vendor GPU support moves to LibGPUDriverInteract; the common Plugin SDK remains for IR and other
  independent extensions.

## Sequence

1. **Inventory the complete target.** Account for every device selector and profile, model and
   firmware variants, feature paths, hardware dependencies and unresolved differences. Preserve
   pinned source references and distinguish Linux mechanisms from Windows mechanisms.
2. **Extract the existing device implementations.** Move the current Claw and Ally hardware paths
   into the library without losing input, motion, haptics, OEM controls, power, fans, charging,
   lighting, suspend/resume or cleanup. These are the first migration increment, not the complete
   support target.
3. **Define the library API from those implementations.** Keep capabilities, input samples, command
   outcomes and lifetime operations. Remove WSGM UI placement, host adapters, plugin manifests and
   package-loading requirements. Account for common Plugin SDK references to the old Device SDK.
4. **Implement every remaining inventory family.** Group work by real transport and protocol
   relationships, retain variant-specific quirks, and develop Windows equivalents for Linux-only
   paths. Track each model and feature through implemented and hardware-observed states.
5. **Integrate WSGM directly.** Replace its DevicePluginRuntime/package selection with the library
   while retaining application policy, profiles, AutoTDP, Steam/Overlay parity and exclusive
   hardware ownership. Remove device-package installer dependencies after parity is achieved.
6. **Publish and maintain.** Release the library independently, document the public API, accept
   bounded device PRs, and keep the inventory and regression evidence current.

## Completion

The migration is complete when the full HC/HHD target is accounted for in implementations, existing
WSGM functionality is preserved, required Windows transports and dependencies are delivered, and
device/feature validation is reported accurately. An inventory entry, API declaration or stub does
not count as implemented device support.
