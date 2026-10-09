# MSI Claw plugin provenance

Source revision: `HW-2026-09-03` (Claw 8 AI+ A2VM, the reference unit)

## The other Claw models

On 2026-09-28 the package was widened from the Claw 8 AI+ A2VM to the whole family Handheld
Companion 1.3.1.6 supports, from the decompiled build in `_ref/HandheldCompanion`, and then reviewed
against that source with HHD (`_ref/hhd` at `5b49c5d9`) as a cross-check. Nothing below was run on
hardware; every fact is source evidence, and each per-model fact lives in one row of
`ClawModels.cs`. Paths are relative to `_ref/HandheldCompanion/source/HandheldCompanion`.

- Identity: `HandheldCompanion.Devices/IDevice.cs`, `GetCurrent`, switches on the upper-cased
  baseboard manufacturer `MICRO-STAR INTERNATIONAL CO., LTD.` and then the baseboard product:
  `MS-1T41` ClawA1M, `MS-1T42` and `MS-1T52` ClawA2VM, `MS-1T8K` ClawBZ2EM, `MS-1T91` ClawCG3EM. It
  reads no SKU, so the plugin does not match `1T52.1` either. The plugin reads the manufacturer from
  `Win32_BaseBoard`, the field the manifest's hardware rules use.
- Shared protocol: ClawA2VM, ClawBZ2EM (`HandheldCompanion.Devices`) and ClawCG3EM
  (`HandheldCompanion.Devices.MSI`) derive from `ClawA1M` and override only power figures, the User
  scenario and the boost write. MSI_ACPI `Get_Data`/`Set_Data`, `Get_AP`, `Set_Fan`, the scenario
  byte at `0xD2`, charge at `0xD7`, full speed at `0x98`, the MCU mode switch `0F 00 00 3C 24`,
  `SyncToROM` `0F 00 00 3C 22` and the `MSI_Event` codes 0x29 and 0x58 are one implementation for
  all of them.
- Readback: HC's `WMI.Set` ignores the result and HC never reads a limit, fan table or RGB profile
  back (`HandheldCompanion/WMI.cs`; `_ref/HandheldCompanion/FINDINGS.md`). The plugin writes the
  same way: a write the transport accepted is applied, a matching read upgrades it to verified, and
  a mismatch publishes the written value without a rollback. A setter's response status is not
  checked; a getter needs HC's success flag (`WMI.Get`'s `readSuccess`).
- Provider and gates: HC binds `MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'` (`ClawA1M.WmiPath`); the
  plugin binds that path and falls back to the first active instance only when it does not resolve.
  HC calls `Get_WMI` (block 1) only to compute an unused `isNew_EC` and never calls `Get_EC`, so the
  plugin reads both for diagnostics alone and a refusal of either changes nothing. Power and fan
  recovery entries bind to the SMBIOS BIOS version (MSI ships EC updates in its BIOS packages),
  which reads the same on every start. Earlier builds bound to the EC and MSI_ACPI versions; the
  reference unit's read `ec:1T52EMS1.109;msi-acpi:8.0`. Such an entry is restored once when a start
  reads that binding again and dropped otherwise. An entry bound to another BIOS is dropped with a
  warning; a failed restore is kept and written again only after an explicit command.
- MCU revision: HC reads the HID collection's `Attributes.Version`; the plugin takes the MCU
  collection's release number the same way, and records the PnP `REV_` field for diagnostics only.
- Power: each class's `cTDP` and `TDPOverrideValues` (`{PL1, PL1, PL2}`), after
  `PerformanceManager.RequestTDP`'s clamp to `cTDP`. ClawA1M 20-45 W with 20/20, 30/30 and 35/35;
  ClawA2VM 8-37 W; ClawBZ2EM 15-35 W with 15/15, 20/20 and 28/28; ClawCG3EM 20-37 W with 20/20 (its
  declared 15/15/20 clamps to 20), 25/37 and 30/37. Full Power at each model's ceiling is WSGM's
  addition. HC writes Slow (0x50), then Fast (0x51), 200 ms apart (`PowerType`: Slow 0, Stapm 1,
  skipped on the OEM path, Fast 2), and its TDP watchdog writes them again every 3-5 s while the
  reported limits differ; the plugin does both, on its 10-second observation pass.
  `ClawBZ2EM.set_short_limit` writes the boost value to `0x51` and then `0x52`; the plugin does the
  same on every pair write, and reads `0x52` once before its first write so a restore can put it
  back.
- Scenario: `GetShiftValue` reads `Get_AP` block 0, `data[2]` (response byte 3), and `SetShiftValue`
  writes `Set_Data` 0xD2. Targets use `SetShiftMode`'s arithmetic: `ChangeToCurrentShiftType` is
  `((v & 0xC3) | 0xC0) & 0xFC` plus the mode, `Deactive` is `((v & 0xC3) | 0x80) & 0xBF`.
  `ClawCG3EM.GetShiftModeValue(User)` returns 6 where ClawA1M returns 3. HC's profile handler also
  applies Sport (AC) or Comfort (DC) to any profile without a preset scenario; WSGM has no profile
  event for manual or AutoTDP limits, so it changes the scenario only for a preset or a user choice.
- Charge: `SetBatteryChargeLimit` keeps bit 7 and writes the percentage in the low seven bits, and
  `SetBatteryMaster` sets or clears bit 7 from its enable setting; the limit is enforced only with
  bit 7 set. The plugin writes `0x80 | percent`, and offers HC's 60, 80 and 100 (`BatteryBypassStep`
  20).
- Fans: `SetFanControl` reads `Get_AP` block 1, `data[0]`, sets or clears bit 7 and writes 0xD4;
  `SetFanFullSpeed` does the same with 0x98 through `Get_Data`. The plugin reads and writes both the
  same way. The curve is where HC and HHD disagree: HC's `SetFanTable` writes eight bytes of duty
  scaled to 150 at fixed temperatures and never touches `Set_Temperature`, while HHD's six
  temperature/duty points in percent match the tables the reference unit reports through
  `Get_Fan`/`Get_Temperature` (byte 1 of `Get_Fan` is a flag there, where HC would write a duty).
  The plugin keeps the layout the reference unit reports.
- Lighting and paddles: `ClawA1M.deviceVersions` maps the MCU firmware to the RGB profile address
  and the M1/M2 DirectInput mapping addresses, choosing the row nearest the reported `bcdDevice`:
  0x0163 and 0x0211 use `0x01FA`, `0x007A` and `0x011F`; every other row `0x024A`, `0x00BA` and
  `0x0163`. An unreadable revision takes the reference unit's row. HC writes the RGB profile without
  reading it, so the plugin offers lighting whenever the MCU collection is present. `Open` and every
  mode change write `GetM12` for M1 and M2 (`0F 00 00 3C 21 01 <addr> 02 01 00`) and `SyncToROM`
  before `SwitchMode`, with HC's 300/500/500/500 ms sleeps; the plugin does the same before it takes
  the controller. HHD writes the same addresses with a longer payload.
- Controller: `DClawController` reads the DirectInput state (buttons 0-11 and 15-16, X/Y/Z/Rz
  sticks, Rx/Ry triggers, POV 0) for every Claw, and `ControllerManager` admits PIDs 0x1902 and
  0x1903. The plugin's fixed-offset decoder is that mapping laid out as measured on MS-1T52; the
  other models go through the HID descriptor with `HidP_GetUsages` and `HidP_GetUsageValue`. HC's
  skip of DirectInput's centred pre-report state has no raw-HID counterpart; the reader skips the
  MCU's all-0xFF first report instead. The paddles keep the reference unit's order, which HHD shares
  and HC has reversed. HC's `Close` and app exit switch the controller to XInput; the plugin
  releases to XInput too. The MCU is matched by HC's usage page and usage alone.
- OEM buttons: `MSI_Event` codes 0x29 and 0x58, masked with `& 0xFF`. HC repairs a missing class by
  deploying `msiapcfg.dll`, setting `WmiAcpi\MofImagePath` and restarting `ACPI\PNP0C14`; the plugin
  does the same except the deployment, since the library is MSI's and cannot be redistributed. HC
  also declares silenced keyboard chords `LWin+G` ("QS") and `LWin+Tab` ("QS, Long-press") that
  raise its QS button; the plugin raises QuickAccess for both, without needing `MSI_Event`. Other
  `MSI_Event` codes are ignored, as in HC.
- Motion: `Resources/Devices/Claw*.json`. Every model swaps to (X, Z, Y); gyro signs are (1, 1, -1)
  throughout; accelerometer signs are (-1, -1, 1) on ClawA1M, (1, 1, 1) on ClawBZ2EM and (1, 1, -1)
  on ClawA2VM and ClawCG3EM. HC picks the gyrometer and the accelerometer independently at runtime:
  the WinRT default first (`HandheldCompanion.Sensors/IMUGyrometer.cs`, `IMUAccelerometer.cs`), the
  legacy sensors named by `WindowsGyrometerFields` (ClawA2VM, ClawCG3EM) when WinRT finds none. The
  plugin follows that order through the Sensor API (the WinRT defaults are the standard
  `SENSOR_TYPE_GYROMETER_3D`/`ACCELEROMETER_3D` sensors), matches physical sensors by friendly name
  and fields as `WindowsSensorManager` does, and runs gyro-only without an accelerometer. MS-1T52
  takes its measured physical pair first. A gyro axis at or beyond 2000 dps is zeroed, HC's default
  threshold (`IMUCalibration`). The `SENSOR_EVENT_DATA_UPDATED` id is the one in `sensors.h`.
- Rumble: `DClawController` sends 193 for any nonzero motor value, polled every 100 ms, only when
  the device is exactly ClawA1M; every other model writes the motor values directly. The plugin
  writes at most once per 100 ms on the A1M and every 4 ms elsewhere, delivering a state that
  arrives inside the interval when it ends; a stop goes out at once. The A1M declares 10 frames a
  second and a 100 ms minimum pulse.
- Package id: `wsgm.device.msi.claw-8-a2vm` became `wsgm.device.msi.claw`. The curated record's
  `replaces` has Setup delete the old package.
- Not carried over: `Open()` writing default power limits and `SetShiftMode(Deactive)` (a hazard
  noted in `_ref/HandheldCompanion/FINDINGS.md`), `Close()`'s `SetFanFullSpeed(false)` (the recovery
  journal restores the captured fan state instead), HC's default eight-byte fan table on leaving
  software fan mode (it does not fit the reported layout), and deploying `msiapcfg.dll`.
- HHD cross-check where it disagrees with HC, the plugin following HC: triggers (HHD has Rx right,
  Ry left), power tables (HHD caps the A2VM's PL1 at 30 W with PL2 37, gives the A8 separate SPL,
  SPPT and FPPT, and has no MS-1T91 entry), and lighting colours (HHD writes one colour to all nine
  triples).

On 2026-09-18 the reference unit carried BIOS `E1T52IMS.114` (released 2026-09-17, still shipping EC
`1T52EMS1.109`) and MCU firmware `0230` from MSI's controller updater `2608_3101`, which lists no
changes. The plugin had refused controller ownership and lighting on `0230` through the exact `0229`
gate, and its stale controller journal entry, bound to `mcu:0229`, then blocked the controller
behind an identity nothing could match. Both gates and the journal binding were removed; the
revision is recorded in the identity snapshot only. Read on the unit that day, unelevated, through
the same `ReadProfile` request the plugin sends: the RGB profile at `0x024A` answered on `0230` with
the reviewed 32-byte shape (`00 01 09 03 64 ...`), so lighting now verifies that shape at acquire
instead of a revision. The DirectInput pad report on `0230` still carries only the first ten bytes
at rest and the MCU vendor collection emits nothing unsolicited, so the controller firmware still
exposes no IMU over HID; motion stays on the Sensor API path below. The charge-limit service faulted
at every start on BIOS `114` with its plain 60-100 range check: register `0xD7` read `0x80`, logged
by the deployed build that day. Handheld Companion treats bit 7 as the "Battery Master" enable flag
and the low seven bits as the percentage, so this is the flag set with the percentage reset to zero
by the BIOS update. The plugin now masks the read, carries the flag through writes, publishes an
out-of-range percentage as unknown, and leaves the capability writable so the configured limit is
applied over the reset. **The write of `0x80 | percent` and its readback are source-derived and
await the maintainer's confirmation on the unit.**

On 2026-09-08, the existing ordered power-pair transport was connected to the SDK's optional
coordinated command. AutoTDP uses equal PL1/PL2 targets within the existing 8-37 W bounds. New
fake-transport tests cover raising, lowering and failed-readback rollback. This is software
validation of the existing transport, not a new attended hardware pass.

The 2026-09-05 keyboard comparison against HandheldCompanion revision
`5c94abca83f8711ff5620906871b31a41c76bf05`, `Helpers/FirmwareWorkarounds.cs`, found that synthetic
Win releases also need the extended-key flag. The incomplete firmware sequence cannot be handled by
ordinary shortcut remapping. On 2026-10-01 the maintainer reported that the orphan-only rewrite in
`24368aef` opened Game Bar alongside WSGM, directed restoration of the previous state machine, and
supplied `Z:\BlockWinG.zip`. Its source and decompiled binary suppress both G down and orphan G up,
keep the shortcut active and consume physical releases after synthetic Win release. The previous
WSGM implementation and regression cases are restored. This restoration has not had a new attended
Claw suppression pass.

Power-preset data was checked on 2026-09-05 against HandheldCompanion commit
`5c94abca83f8711ff5620906871b31a41c76bf05`: `Devices/MSI/ClawA2VM.cs` supplies the 8/8/9, 17/17/18
and 30/30/31 W overrides; `ClawA1M.cs` and `Properties/Resources.resx` supply the names and Windows
modes. WSGM's sustained/slow pair maps these to PL1/PL2 8/9, 17/18 and 30/31 W. AC firmware targets
follow HC's Eco/Green/Sport choices, with Comfort on battery. Full Power is a WSGM addition using
37/37 W, Best Performance, and Sport on AC or Comfort on battery. These are independently
implemented through the SDK. HC's CPU boost and Intel Endurance settings are outside this shortcut.
This is source evidence and fake-transport validation, not a new attended hardware measurement.
Existing power transport limits and rollback behavior are unchanged.

The package is first-party code licensed under the MIT License. It is the reference implementation
of the WSGM Device SDK — the plugin other device plugins are expected to be read against and copied
from — which is why it is permissive rather than carrying WSGM's own GPL-3.0-or-later. A plugin
links only `WSGM.Device.Sdk`, never WSGM, so nothing here obliges a derived plugin to any licence.

Required third-party notices ship in `THIRD_PARTY_NOTICES.md`.

## Hardware knowledge in this package

Every register, report layout and WMI method here was established by observation on a physical MSI
Claw 8 AI+ A2VM, not from vendor documentation. Two consequences:

- The revision above identifies the hardware generation the behaviour was confirmed against. A
  different Claw model is a different device and is not claimed to be supported by detection.
- Intel graphics-driver controls, and the evidence behind them, live in the `wsgm.gpu.intel` plugin.
- Intel's IO/sensor driver exposes the STMicroelectronics LSM6DSO `Physical Accelerometer` and
  `Physical Gyrometer` through the legacy Sensor API as custom sensor type
  `e83af229-8640-4d18-a213-e22675ebb2c3` on the `VID_8087&PID_0AC2` HID collection. Their live
  values are `VT_R4` fields 7, 8, and 9 under property-set `b14c764f-07cf-41e8-9d82-ebe3d0776a6f`,
  in g and degrees/second respectively. Field 34 is the gyrometer's opaque `VT_UI4` hardware-report
  counter: it advances for stationary samples too. The gyrometer advertises a 10 ms minimum report
  interval (100 Hz); the accelerometer advertises 2 ms, but its synchronous `GetData` can still wait
  about 200 ms for a changed report at rest. Combined reads therefore acquire and qualify the
  gyrometer first, so duplicate polls do not incur an accelerometer read; a delayed accelerometer
  can make the paired gyro report and timestamp older by at most that wait. The application-axis
  transform for both die-aligned sensors is `(raw X, raw Z, -raw Y)`; the Steam Deck encoder
  reverses that once when filling the controller's raw IMU slots. WinRT does not project the
  accelerometer and its gyrometer event path suppresses unchanged reports.
