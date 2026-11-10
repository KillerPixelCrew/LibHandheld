---
name: libhandheld
description:
  Implement or review LibHandheld exact device detection, family protocols, capability commands,
  telemetry and recovery. Use for handheld library changes; host profiles, UI and AutoTDP stay in
  the consumer.
---

# LibHandheld contributions

Paths here are relative to the LibHandheld repository. If opened through a consumer's skill alias,
resolve the alias to this owning repository first. Read AGENTS.md, CONTRIBUTING.md, docs/README.md,
the relevant contracts and the family's AGENTS.md and PROVENANCE.md before changing protocols.

## Ownership and conventions

HandheldDevice and Contracts expose semantic operations. Families own exact hardware facts;
Internal/Devices, Internal/Transports and Internal/Windows hold shared mechanisms. Detection is pure
and exact. Keep host profiles, AutoTDP, virtual targets and UI policy outside the library. Follow
the owning repository's formatting settings. C# contributions use expanded braces, var locals with
explicit construction types when needed, named literal arguments and no trailing commas in multiline
lists. Document public contracts and preserve protocol evidence. Avoid forwarding wrappers and model
forks where existing profiles and services can express the measured difference.

## Commands and state

Separate requested/accepted values, firmware observations and defaults. A stock profile reading must
not silently become a user-owned IPF request during acquisition, restore or path switching. A
fan-only command must preserve the power pair on every admitted path, or explicitly explain why the
operation is unavailable there. Keep advertised choices, result meaning and AutoTDP integration
consistent; a presentation label alone must not determine native power behavior.

Report Applied only for the contract actually fulfilled. Unknown companion limits must not turn a
command into a silent success without dispatch. Do not gate ordinary writes on readback. If a safe
packet requires retained bytes or a paired value that cannot be resolved, report that prerequisite
truthfully and preserve user intent. Validate coupled PL1/PL2 values together: silently clamping a
boost while acknowledging the requested preset breaks consumer matching and can erase assignments.
Choose a compatible path/mode or expose the constraint rather than pretending the pair was applied.

Track dispatch at every physical output. A no-op RAM write does not mean a following HID write is
also a no-op. Mark dispatch immediately before each native effect; failure or cancellation after it
remains uncertain. Do not clear uncertainty or resend merely because another transport cache agrees.
On startup and wake, a retained lighting profile is not proof that the separate LED output was lit.
Publish output failures without claiming Owned/successful values, and retain an explicit recovery
path without automatically repeating an uncertain write.

## Recovery and evidence

Use the existing firmware-bound journal before temporary mutations. If product policy restores a
fixed safe mode instead of the original, record that restore target; skipping capture loses crash
recovery. Distinguish suspend retention from full release. A swallowed cleanup error cannot become
Idle or a clean engine stop. Keep accepted permanent edits separate from owned temporary effects.
Scope behavior changes to the measured model instead of changing an entire family implicitly.

Trace acquisition, host restore, user commands, telemetry, suspend/resume, path changes and release.
Read fixtures for missing readings, no-op primary writes followed by secondary effects, partial
writes, failed relight, failed cleanup and requested/published power pairs. Add meaningful
regression coverage when changing behavior, subject to the maintainer's validation timing.
Input/telemetry must allocate and log nothing per sample. HardwareVerified requires attended family
provenance; compilation, fixtures and source review do not establish a hardware pass.
