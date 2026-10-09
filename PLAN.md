# LibHandheld implementation plan

## Accepted direction

Windows-first .NET, supporting the complete HC/HHD target. HC is the primary Windows reference, HHD
secondary and OpenGamepadUI tertiary. Hardware implementations live here; application policy and UI
remain with consumers. Device support is contributed as native library source and fixtures, without
WSGM SDKs, manifests or installed device packages. GPU mechanics belong to LibGPUDriverInteract.

## Applied extraction

- Independent `HandheldDevice` API and typed contracts, with borrowed observer publications and
  consumer-owned lifecycle generations.
- Five Claw and four Ally definitions with migrated native transports, input, motion, haptics,
  power, fans, charging, lighting, OEM controls and restoration paths.
- Private helpers and original glyph/notice bytes retained; 22 migrated regression source files.
- WSGM directly uses the library through its adapter/runtime, retaining profiles, AutoTDP, HidHide,
  virtual input, OEM policy and existing family recovery-state identities.
- Separate WSGM Device SDK assembly and device package projects removed. Common Plugin API 5 retains
  shared historical namespaces in its single assembly; dependent plugins need rebuilding.
- Setup uses exact library detection and native controller availability. Device Lab contributions
  produce source, fixtures and provenance rather than device packages.

Source extraction is applied. Current build, deployment and manual acceptance evidence belongs in
WSGM's delivery record. No new hardware pass is claimed by this plan; automated tests remain subject
to the maintainer's manual-first acceptance sequence.

## Remaining full target

1. Resolve ambiguous board, CPU and firmware variants against the pinned inventory, preserving HC >
   HHD > OpenGamepadUI precedence and lower-priority observations.
2. Implement remaining families by actual protocol relationships, including required Windows EC,
   PawnIO, SMU, HID and WMI transports. Keep unknown decoders explicitly unimplemented until
   concrete evidence permits implementation.
3. Integrate each family through the existing typed API and WSGM policy path. Include runtime
   controller transitions, suspend/resume, restoration and dependencies, not just identity rows.
4. Add focused protocol fixtures and record attended device/feature validation independently.
5. After testing, make library distribution and CI access available and maintain bounded device PRs.

The inventory contains 201 HC/HHD source records; it is not a count of unique physical devices. The
nine seed definitions do not complete that target. Inventory entries, declarations, selectors and
scaffolds do not count as implemented native support.
