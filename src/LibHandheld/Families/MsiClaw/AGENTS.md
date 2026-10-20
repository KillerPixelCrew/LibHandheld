# MSI Claw library contributor instructions

## Scope and sources of truth

These instructions apply to `external/libhandheld/src/LibHandheld/Families/MsiClaw/**`.

This project is the MIT-licensed family implementation for the MSI Claw family: Claw A1M (`MS-1T41`), Claw 7 AI+ A2VM
(`MS-1T42`), Claw 8 AI+ A2VM (`MS-1T52`), Claw A8 BZ2EM (`MS-1T8K`) and Claw 8 EX AI+ CG3EM (`MS-1T91`). Only the
Claw 8 AI+ A2VM has hardware evidence; the other rows were built from Handheld Companion 1.3.1.6
(`_ref/HandheldCompanion`), which derives every Claw class from `ClawA1M` and shares its WMI, MCU, charge, fan and
lighting protocols. Read the library README, `PROVENANCE.md`, the relevant tests, and the current
implementation before changing behavior. `PROVENANCE.md` records the reference unit's revision and motion evidence
and cites each HC-derived model fact.

- HC 1.3.1.6 is the reference for the models without hardware evidence. A Device Lab observation on that model beats it.
- Every per-model fact lives in `ClawModels.cs`. Correct a model by changing its row, never by adding a model check
  elsewhere. A measured A2VM behaviour applies to another model only where HC shows the same code path.

## Library ownership

The family implementation lives in LibHandheld. Public contracts are in its Contracts folder; WSGM owns host policy, profiles and both user surfaces. Use the library tests and the parent manual-first validation policy. Device Lab scaffold emits source contributions, not installable device packages.

## Exact device boundary

Match the machine as HC's `IDevice.GetCurrent` does: baseboard manufacturer `MICRO-STAR INTERNATIONAL CO., LTD.` and
a baseboard product in `ClawModels`. The SKU is recorded, never matched. Family ID `msi-claw` and the
per-model definition IDs (`ms-1t41` ... `ms-1t91`) identify software records, not the machine. WMI-backed services need
only the MSI_ACPI provider, bound at HC's instance path. Power and fan recovery entries bind to the SMBIOS BIOS
version (MSI ships EC updates inside BIOS packages). `Get_WMI` and `Get_EC` are read for diagnostics,
and may fail without consequence. Legacy journal entries are not migrated. An entry bound to another BIOS is dropped
rather than restored. Unresolved entries are kept and never written automatically; an explicit command re-arms them.
No reconciliation outcome blocks a service. MSI USB VID `0DB0` with the supported PIDs gates the controller. Never
gate on a firmware revision, EC or MCU (the HID collection's release number): MSI ships both through Windows Update and
its updater, and the 0229 gate refused controller ownership and lighting on every unit that moved to 0230. Record
revisions for diagnostics. MCU addresses (lighting and the paddle mapping) follow HC's nearest-firmware table.

Detection must remain side-effect free. Identity is captured once and reused on resume and controller changes;
a command reads the AC line and revalidates service availability, cancellation, deadline and range before
access. Install the per-instance diagnostic sink before the first hardware read.
Report truthful outcomes: a write the transport failed is indeterminate, never success.

## Readback

Follow HC: it writes MSI_ACPI and the MCU and never reads a value back to confirm it. A read never gates a write or a
control, and a mismatch never triggers a rollback or a second write. A write the transport accepted is applied, and the written value is
published as observed for the rest of the cycle. A read that fails at acquire leaves the value unknown until the first
write, and a command whose original state cannot be read is written without a journal entry. An uncertain (failed)
write is never retried.

## Lifecycle and service behavior

- Serialize commands with observation so reads cannot race writes. Preserve whole-set publication and active-owner admission.
- Keep periodic observation inside the host freshness window. A service read failure may degrade that service but must
  not kill the observation loop or unrelated services.
- Retract physical/OEM/descriptors if startup cannot establish the supported device. Treat OEM,
  power/charge/fans/telemetry, lighting, motion, controller, and chord suppression as independently
  degradable.
- Trace transitions, decisions, and keyed state changes only. Do not log every HID or motion report; cancellation is
  diagnostic, not an error.
- Stop and disposal must be bounded, idempotent, and honest about incomplete cleanup.

## Mutation invariants

All hardware protocols are bounded and allowlisted at their call sites. WMI calls are serialized, the transport admits
only `Get_*`/`Set_*` names with 32-byte packages and the established timeout. A getter needs the success status; a
setter's response is not checked, as in HC. Preserve unknown bytes in stateful read-modify-write formats such as fan
and lighting payloads; power and charge use zero-filled envelopes with the value in byte 1.

- Power: keep PL1 and PL2 within the model's `cTDP` range (8-37 W on the A2VM), the clamp HC applies before every write.
  Write 0x50 then 0x51, 200 ms apart, as HC's `PerformanceManager` does; the BZ2EM also gets the boost value at 0x52
  straight after 0x51. Every PL1 or PL2 command carries the other limit as WSGM decided it (`DevicePowerPair.TryResolve`
  refuses PL1 above PL2); write the pair as given and never derive one limit from the other. While the EC
  reports limits other than the last requested pair, write that pair again on the 10-second periodic observation
  pass only (HC's TDP watchdog); a failed write disarms it. Capture `0x52` for restore when it reads; a refused
  read is unknown.
- Scenarios: read the SHIFT byte where HC does (`Get_AP` block 0, data[2]) and write it through `Set_Data` 0xD2 with
  HC's arithmetic: `ChangeToCurrentShiftType` for a mode, `Deactive` for inactive. Presets map Super Battery/Balanced/
  Extreme Performance to Eco/Green/Sport on AC and Comfort on battery. User is 3, or 6 on the CG3EM. Select or restore
  the scenario before the pair.
- Charge: 60, 80 or 100 percent (HC's 20 % step) in the low seven bits of `0xD7`, with bit 7 (Battery Master) set, since
  the limit is only enforced with it. A read outside 60-100 is published as unknown with the capability still
  writable. Do not restore a successful choice on normal stop.
- Fans: one six-point curve goes to both channels in the table layout the reference unit reports through
  `Get_Fan`/`Get_Temperature`, which differs from HC's eight-byte `SetFanTable` (see PROVENANCE.md). The custom flag is
  read from `Get_AP` block 1 and written to 0xD4, full speed through 0x98.
- Lighting: write HC's 32-byte profile over the bytes last read, replicating the three logical zones, and keep the
  write-rate limit. Lighting is offered whenever the MCU collection is present; a profile that does not read back in the
  known shape only leaves the state unknown until the first write. Do not revert a user choice on normal stop.
- Controller mode: write HC's M1/M2 DirectInput mapping and `SyncToROM` before the first take of a run (the ROM keeps
  it across sleep, so a wake does not rewrite it), stop source/output first, journal XInput as the release mode,
  switch, wait for re-enumeration, and identify the same physical device through `DEVPKEY_Device_LocationPaths`.
  Publish the XInput identities found before the switch with the settled DirectInput ones: the firmware returns to
  XInput in every sleep, and that pad must already be hidden when it reappears. A topology's identities are the
  gamepad HID collections plus `HidDevices.KnownControllerNodes` for every Claw product ID at the pad's location,
  the set every other family hides. On release switch to XInput, as HC's `Close` does, whatever mode the controller was found in.
- Power, fans, and controller mode are temporary. Capture the first original value in the shared `recovery.json` before
  mutation when it can be read, restore power and fans only on the same BIOS binding, and restore controller mode on
  any MCU revision. A restore is complete once its writes went through.
- Intel graphics-driver controls (variable refresh, Endurance Gaming, shader download, shared GPU memory, driver
  VSync) belong to LibGPUDriverInteract.

## Input and motion invariants

- Preserve the measured DirectInput report layout on MS-1T52: byte 7 bit 4 is left/M1 and bit 3 is right/M2. Assert
  the two bits separately so a swapped mapping cannot pass. Every other model decodes through the HID descriptor with
  HC's `DClawController` button indices and the measured paddle order. Skip the MCU's all-0xFF first report.
- OEM buttons: MSI_Event codes 0x29 and 0x58 as HC maps them; every other code is ignored, as in HC. Where MSI_Event
  is missing, repair it as HC does (MOF path, `ACPI\PNP0C14` restart), but only with MSI's `msiapcfg.dll` already
  installed; it cannot be redistributed. Events carry no release, so the library's `OemButtonLatch` holds each press for
  HC's 200 ms `KeyPressDelay`.
- Chord handling belongs in this family. Preserve the pre-`24368aef` state machine, checked against
  the maintainer's `BlockWinG.zip` PoC. An incomplete firmware sequence can omit a target edge:
  intercept G down and orphan G up with Win held, and keep repeat/release suppression through an
  accepted synthetic Win release. Do not narrow it to orphan-up-only detection again. Win+Tab is
  the long QS chord; modified Win+Tab and modified orphan releases pass through. Injected input
  passes through. A QS from MSI_Event and the chord within 500 ms are one press. The callback must
  remain bounded, allocation-light, and free of I/O and logging.
- Keep the x64 `INPUT` ABI at 40 bytes with its 32-byte union, including for keyboard-only injection. A smaller record
  makes `SendInput` reject the synthetic Win release and the hook pass the firmware chord through. Keep the layout
  regression tests.
- Synthetic left/right Win events must carry `KEYEVENTF_EXTENDEDKEY`; dummy-key events must not. Source comparison and
  layout tests do not establish that desktop Game Bar suppression works.
- Motion: pick the gyrometer and the accelerometer independently in HC's order, the standard Sensor API sensors behind
  WinRT's defaults first, then the "Physical" sensors where HC's JSON declares their fields; MS-1T52 takes its measured
  physical pair first. Match physical sensors by friendly name and fields, as HC does. A missing accelerometer leaves a
  gyro-only source. Deduplicate by the hardware counter where the gyrometer has one, otherwise by report timestamp. Do
  not move to WinRT: its projection leaves finalizable objects on every sample. The library's `LegacyMotionStream` reads
  them and its `MotionSampleBuilder` zeroes a gyro axis at or beyond 2000 dps before anything else, as HC's threshold
  does; motion streams for as long as the family service owns the controller.
- Apply the axis transform exactly once: HC's shared swap `(raw X, raw Z, raw Y)` times the model's signs, which
  gives `(raw X, raw Z, -raw Y)` on the A2VM.
- Rumble is proportional except on the A1M, where HC's `DClawController` drives each motor on or off at 193 and at most
  once per 100 ms, with haptic capabilities to match (10 frames a second, 100 ms minimum pulse). The host paces output
  to the declared frame rate and never holds back a stop; the family service writes each changed state once.
- Preserve the measurement-derived stationary gyro bias behavior: approximately 200-report windows, subtraction without
  deadband, rest gates, and agreement across three separated windows before distant-bias reacquisition. Preserve
  resampling and reset semantics; do not clamp away a valid distant correction.

## Glyphs, tests, and evidence

The glyph profile names each asset by `assetId`, which is also its file name under `glyphs/assets`. When artwork
changes, update the declared `viewBox` or pixel dimensions, the source revision, and the notice; keep the authored
artwork as upstream drew it rather than reformatting it. Keep all manifest assets packaged.

CI is software-only. Any claim about WMI, HID, Sensor API, controller re-enumeration, fan/lighting payloads, or power
behavior requires an explicit attended Device Lab run on that model and a provenance update. Add focused regression tests for each changed identity gate, protocol byte, timeout, rollback, journal, mapping,
transform, calibration, ABI, and package invariant. Preserve repository `.editorconfig` conventions.

