# Full device implementation

Active goal: implement every HC/HHD target through real Windows backends and deliver the complete
WSGM integration. Source priority: HC > HHD > OpenGamepadUI. Work branch:
`chore/device-integration-rework`; repositories are public and automatic CI remains off until
explicitly enabled.

## Tasks

- [x] **Shared device abstraction and public operations.** Detect the current model or accept an
      explicit model; route TDP, fans, charging, lighting, input, motion, haptics and restoration
      through family backends. A new variant of an existing protocol should require a model profile
      rather than another lifecycle implementation. Preserve the nine existing Claw/Ally backends.
- [x] **Lenovo Legion Go families.** Implement exact variants, WMI and indexed EC controls, raw HID
      input, IMU, lighting, haptics, OEM events and lifecycle/recovery.
- [x] **Privileged Windows transports.** Deliver checked PawnIO indexed/ACPI EC and AMD SMU access,
      Intel power access, required modules and installation dependencies.
- [x] **AYANEO, GPD and Zotac families.** Implement their actual shared EC/controller protocols and
      model-specific variants, including modular AYANEO 3 behavior and GPD remapping mechanisms.
- [x] **OneXPlayer and AOKZOE families.** Implement legacy indexed EC, X1, X2 WMI and distinct HID/
      serial generations with their firmware predicates and complete supported controls.
- [x] **Valve and remaining HC/HHD targets.** Implement Steam Deck HID, controls and power access,
      generic physical-controller/sensor paths and Windows equivalents for HHD-only mechanisms.
- [x] **Complete delivery and coverage.** Map every source record to its concrete implementation,
      integrate model capabilities/dependencies in WSGM/setup, format/build/deploy and record actual
      testing separately. Audit omissions; detection rows, declarations and stubs do not count as
      support.

## Evidence

The inventory contains 201 source records, including aliases and overlapping selectors.
Implementation coverage is maintained in `inventory/implementation-coverage.json`; hardware
observations remain separate. New automated tests and test-bearing gates follow the maintainer's
manual-first policy.

Earlier delivery: WSGM implementation commit f1e212d8 consumes LibHandheld 0499bd0. The full setup
and development installation are version 2.1.0.1660. Whole-solution Release compilation passed with
zero warnings/errors; Rider cleanup and Steam asset checks passed. Native libraries, helpers and
installed assembly hashes match the staged output. Automated tests remain deferred; no new handheld
hardware acceptance is claimed. Later tracker-only commits do not change this delivered source
snapshot.

HC 1.3.3.1 parity follow-up, 2026-10-12: 112 exact definitions now select 106 authored glyph
profiles. Native packet, motion/rear mapping, processor settings, observations and haptic
corrections are integrated through both WSGM control surfaces. Release compilation passed without
warnings or errors; the focused regression and rendered UI evidence is recorded in WSGM's parity
audit. Broad vendor/CPU fallbacks remain excluded by exact-identity policy. Hardware purchases and
per-model physical acceptance are not implementation gates; no new hardware pass is claimed.
