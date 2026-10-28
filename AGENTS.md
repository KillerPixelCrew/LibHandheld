# LibHandheld contributor instructions

These instructions apply to the whole library. A family's own `AGENTS.md` adds its domain rules.

## Hardware verification

- `HardwareVerified` is the user-facing statement of what has been tested. Setup shows "hardware
  verified" or "hardware unverified" for the detected model, with a caution for unverified ones, and
  the runtime logs the flag.
- When a definition's integration has been accepted on the physical model in attended sessions, set
  `HardwareVerified = true` on its definition, update the test that pins the flag and record the
  acceptance (unit, date, what was accepted and what remains a fallback) in the family
  `PROVENANCE.md`. Do this in the same change that completes the acceptance.
- Before building a setup or publishing a release, check the flag of every definition worked on
  since the last release against its provenance. Never ship a hardware-accepted model as unverified,
  or an untested one as verified.
- A definition built only from reference sources (for example Handheld Companion) or tested only
  through fixtures stays unverified until an attended run accepts it.
