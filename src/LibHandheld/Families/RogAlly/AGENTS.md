# ASUS ROG Ally family library contributor instructions

## Scope and sources of truth

These instructions apply to `external/libhandheld/src/LibHandheld/Families/RogAlly/**`.

This is the MIT-licensed family implementation for the ROG Ally (RC71L), Ally X (RC72LA/RC72L), Xbox Ally
(RC73YA) and Xbox Ally X (RC73XA). It was built blind from Handheld Companion 1.3.1.6 (the decompiled
Patreon build in `_ref/HandheldCompanion`) and HHD (`_ref/hhd`); nothing in it has a recorded hardware
pass. Read the library README, `PROVENANCE.md` and the tests before changing behavior.

- HC 1.3.1.6 is the primary Windows reference, including default button behavior, ATKACPI, fans,
  power, Aura, rumble and motion. HHD is a secondary cross-check and supplies behavior HC does not
  implement. A Device Lab observation beats both.
- Every per-model fact lives in `AllyModels.cs`. Correct a model by changing its row, never by adding
  a model check elsewhere.
- Every device fact carries a citation, in `PROVENANCE.md` or beside the value. When a lab report
  changes a value, update the citation and the "must confirm" list in the same change.

## Device boundary

Detection matches baseboard manufacturer `ASUSTeK COMPUTER INC.` and the exact baseboard products in
the model table, and stays side-effect free. Creation accepts the exact detected definition and captured identity; start uses that snapshot rather than recollecting SMBIOS.
Every command revalidates service state, cancellation, deadline and range before any write.

## Mutation invariants

- ATKACPI calls go through `WindowsAsusAcpi`, which admits only the `AsusAcpiId` list and validated
  curves. Do not add INIT, WDOG or arbitrary IDs.
- Do not read back writes. HC writes power, fans and Aura blind, and firmware
  that cannot report a value (the Xbox Ally X reports 0 W and refuses the curve query) must still be
  controllable. A refused DSTS query reads as unsupported and never faults a service. After an
  accepted write, publish the written value as `Observed` for the rest of the cycle.
- Power: SPL, then SPPT and FPPT, as HC writes them; mode before limits on restore. Journal the original
  limits and mode before the first write when they can be read, and restore them on stop; when they
  cannot, write anyway and restore nothing, as HC does. A 0 W report is unknown, not a value.
- Fans: eight points, 20-110 °C, non-falling duties, clamped to 99. Journal the original curves when
  they can be read; otherwise write anyway and return the fans to HC's factory tables on stop. Keep a
  failed restore Pending in the recovery record, with no status write. The next start attempts that
  original once. A failed command performs no rollback and leaves the service usable.
- Recovery entries bind to the BIOS version (the controller's to `mcu`). At start an entry from another BIOS is
  dropped, one whose transport is not there yet waits, and nothing blocks a service. Legacy journal
  formats are not migrated; accepted restores retire the current shared recovery entry.
- A write refused for lack of deadline budget has touched nothing: reject it, never fault the service
  or mark a recovery entry failed for it.
- Charge limit and Aura are persistent user choices: never journalled, never reverted on stop.
- Controller tables are HC's bytes in HC's order, written as 64-byte 0x5A feature reports only while
  the controller is managed, journalled first, and replaced by the factory tables on release. They go
  to the last collection with 64-byte features that answers report 0x5A, exactly as HC's `IsReady`
  chooses it, and the button events are read from that same collection. Never select it by usage.
  Every table is sent even when one is refused, as HC does; only a refused M1/M2 table keeps the rear
  keys off. They cannot be read back, so a release whose every table write was acknowledged clears
  the recovery entry, as HC ignores the results; the release itself is best effort and reports
  nothing.
- An uncertain write is never retried. HHD's timed re-send of the controller tables stays out.

## Input invariants

- The pad is read through XInput only, as HC reads it. Discovery returns nothing unless the ASUS
  XInput slot and the pad's device nodes are both present; until they are, the controller reports
  Degraded and the library's `DeviceReconnect` attaches it when they appear. A reader or vendor-collection
  failure is the device going away, never a device fault.
- Vendor codes follow HC's Windows event semantics: 0x93 is a separate Library control and
  0xA7/0xA8 are M2 press/release. `AllyModels.cs` maps the front controls to each model's physical
  layout; keep the HHD disagreement in PROVENANCE.md.
- The keyboard hook claims only the watched F-keys, never injected input, and stays allocation-light
  with no I/O in the callback. Rear keys are watched while the controller tables are applied, and
  always on a model whose `RearKeysNative` says M1/M2 send F18/F17 without them.
- Apply each model's axis maps exactly once, before the gyro-offset correction.

## Validation

CI is software-only. Tests under `external/libhandheld/tests` use fakes for every
transport. Any claim that a transport, code or axis works on hardware needs a Device Lab report
(`wsgm-device report <file.wsgmlab>`) and a PROVENANCE.md update.

