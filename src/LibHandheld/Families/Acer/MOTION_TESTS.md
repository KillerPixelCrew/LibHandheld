# Atlas 8 motion investigation, 2026-10-27

Live evidence from Predator PA08-I51, BIOS V1.03, controller release 0100, with Acer OEM software
disabled. Windows sensor delivery, controller report delivery, firmware motion-to-stick mapping and
raw gyro samples are separate findings. No production motion backend was changed by these developer
probes.

## Working Windows source

Both standard Sensor API devices identify manufacturer `ST_MICRO`, model `LSM6DSOETR3`, behind Intel
HID `8087:0AC2`. Gyrometer type is `09485F5A-759E-42C2-BD4B-A349B75C8643`, accelerometer type is
`C2FB0F5F-E2D2-4C78-BCD0-352A9582819D`. Standard angular-velocity fields 10/11/12 are
degrees/second; acceleration fields 2/3/4 are g. Both advertise a 10 ms minimum report interval.
Their accuracy, resolution and range properties return `0x80070490` (not found). Sensor-module
identity is driver-reported, not a physical inspection of the controller PCB.

After the maintainer confirmed the device was flat on a table, a fresh 30-second capture through the
existing LegacyMotionStream delivered 1,654 fresh reports: 55.12 Hz average arrival rate, 10 ms
median sensor interval, 50 ms p95, 190 ms maximum gap. This stationary delivery rate is not the
maximum sample rate. The subsequent attended movement capture delivered 2,000 reports over 19.99 s
between its first and last sensor timestamp: exactly 100 Hz by that measure. Change suppression at
rest is a plausible explanation; it was not isolated from all other driver/host scheduling effects.

| Sensor axis | Stationary sample mean, degrees/s | Standard deviation, degrees/s | Uncorrected integrated angle over 29.99 s |
| ----------- | --------------------------------- | ----------------------------- | ----------------------------------------- |
| X           | -0.000415                         | 0.09117                       | -0.04671 degrees                          |
| Y           | 0.001563                          | 0.07393                       | 0.00839 degrees                           |
| Z           | -0.000207                         | 0.04102                       | -0.02259 degrees                          |

These are one position/run before LibHandheld's bias correction, not a long-term drift or accuracy
rating. Acceleration stayed at the last reported vector approximately (-0.007117, 0.123185,
-1.007760) g throughout that rest capture; zero observed variation does not establish zero
accelerometer noise. Windows gyro values have differences near 0.00001 degrees/s. Those processed
floating-point increments do not establish the hardware's physical resolution. The
[ST LSM6DSO datasheet](https://www.st.com/resource/en/datasheet/dm00393889.pdf) defines signed
16-bit output with 0.004375..0.070 degrees/s per count depending on full scale. The active scale
behind this Windows driver remains unknown.

Local evidence: `artifacts/acer-motion-properties.log`, `acer-motion-stationary.json`,
`acer-motion-stationary-summary.json`, `acer-motion-stationary-integral.json`,
`acer-controller-motion-moving.json`, `acer-windows-motion-moving-summary.json`. The first capture
included placement movement and is retained as `acer-motion-initial*`; it is not the rest baseline.

## Controller commands and modes traced in Acer's software

`DeviceProxyService`, `DeviceMessage`, `DeviceListener`, `AppServices`, `ControlModeHelper` and the
desktop `SetJoystick` handler establish these paths. All commands below use the vendor output prefix
`0F 00 00 3C` and descriptor-sized reports. Immediate commands have no correlated command
acknowledgement.

| Command            | Source semantics                                      | Observed state                                                       |
| ------------------ | ----------------------------------------------------- | -------------------------------------------------------------------- |
| `24 mode rearMode` | SwitchGamepadMode; wait, then query                   | Mode 1 XInput; mode 2 DInput; rear mode 1 Combination                |
| `26`               | ReadCurrentMode, response `10 00 00 3C 27 ...`        | Original mode bytes `01 00 00 01 00`                                 |
| `2D 01` / `2D 00`  | SetDeviceState(disabled/enabled), used by SetJoystick | Off removes XInput and reports mode 0; on restores mode 1            |
| `2F 01` / `2F 00`  | SetMotionStatus(enable/disable upload)                | No vendor `0x30` motion packets in the tested states                 |
| `2C 00` / `2C 01`  | SetFeatureState(enabled/disabled), inverted payload   | Present in the core assembly; no desktop caller found; not exercised |

The enum additionally names Special=3, Desktop=4, BIOS=5 and TESTING=6. The observations here do not
prove these modes' motion behavior. XInputListener's six vibration values spelling MSIAPP are called
by the firmware-update path; that handshake was not run. `GoToXInputMode` sends legacy
ExitProfileConfig=2. The UI's RescueHandle is an OSD control, not evidence of an additional raw
reader. The desktop gyro configuration performs firmware mapping and calibration; no compiled
desktop consumer of `SetMotionStatus` or `OnMotionDataUpdated` was found. A core-library method
alone does not prove a working raw upload on this firmware.

## USB topology changes are real

| State        | Identity and collections                                                                                     | Result                                                                                    |
| ------------ | ------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------- |
| XInput       | `0502:F901`, vendor `FFA0:0001`, input/output 64; OEM `FFF1:00F5`; QAM `0088:0001`                           | XInput index 0 present                                                                    |
| DInput       | `0502:F90A`, gamepad `0001:0005`, input 64/output 32/feature 48; vendor `FFF0:0040`, input/output/feature 64 | All XInput slots disconnected; gamepad report `01` about 125 Hz                           |
| Joystick off | `0502:F901`, vendor `FFA0:0001` retained; new `0001:0040` input/output 64; OEM and QAM collections retained  | All XInput slots disconnected; no input on `0001:0040` during the short still observation |

Correlate transitions by the captured physical USB location as well as exact
VID/PID/release/usage/report shape. Do not keep using an old handle or assume only PID changes. The
off transition also changes the USB instance identity. Some initial probes queried too early during
reenumeration and stopped with a recovery record. Fresh topology inspection allowed restoration;
mode/profile readback and restored XInput presence were verified afterward. This is evidence for
bounded reconnection and verification, not automatic repeat writes.

The disabled XInput-mode keyboard leaf `F901/MI_02/Col01` is not the DInput keyboard
`F90A/MI_01/Col01`. A future runtime DInput migration must account for OEM shortcuts, front/rear
input, lighting/command transport, detection and restoration across both topologies. Existing XInput
acceptance does not cover it.

## Sensor gate and attended DInput capture

ProfileInfo places the ten-byte SensorModule at RAM offset 41. Its original bytes were
`E0 01 00 04 00 19 32 FF FF 00`. Setting only MotionEnable bit 0 gives
`E1 01 00 04 00 19 32 FF FF 00`. These retained bytes match the desktop's default left-stick gyro
behavior configuration. Other flags, including the model's DisableX/DisableY bits, were preserved;
their firmware behavior was not independently verified. No ROM save or calibration command was sent.

The final attended test switched to DInput, reacquired `F90A`, captured that mode's original module,
enabled and read back the gate after the mode switch, then captured every 64-byte gamepad packet
alongside Windows motion. The maintainer held still for five seconds and rotated through all axes
for twenty.

- 3,174 gamepad reports; measured report rate 125.005 Hz; no overflow/read error.
- 2,444 Windows samples across the capture. During movement its gyro ranged approximately -291..361,
  -219..197 and -183..207 degrees/s on X/Y/Z.
- Only gamepad byte offsets 1 and 2 changed. Offsets 13..18, the candidate DS4 gyro region, stayed
  zero. Offset 22 stayed `0x10`; the rest of candidate acceleration offsets 19..24 stayed zero. A
  fixed gravity-shaped value is not motion evidence.
- The changing left-stick bytes correlated with Windows acceleration X and Y at approximately -0.937
  and +0.913 respectively (nearest-time comparison). Their angular-velocity correlations were weak.
  This supports working tilt mapping; it does not expose a calibrated angular-rate stream or prove
  the controller's physical sensor model.
- The separate `0x30` upload also yielded no packets in prior XInput, DInput and controller-off
  probes. In the XInput and DInput movement trials, source-backed upload enable/disable commands
  completed and normal mode queries answered.

The DS4 field positions are comparison candidates from the
[Linux PlayStation driver](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c),
not a declared Acer motion codec. Do not interpret synthetic/fixed fields as gyro, or reconstruct
raw gyro by undoing the eight-bit stick mapping.

Both DInput and XInput SensorModule snapshots were restored and read back; the original XInput mode
and presence were verified. Upload disable was sent at cleanup; that flag has no sourced getter, so
exact original upload-state recovery is not independently verified. All readers stopped. No further
capture is active.

Local evidence: `artifacts/acer-dinput-motion-attended.log`, `acer-dinput-motion-reports.bin`,
`acer-dinput-motion-timing.json`, `acer-dinput-motion-analysis.json`,
`acer-dinput-gate-restored.json`, `acer-controller-disabled-settled.json`,
`acer-controller-disable-recover.log`, `acer-controller-disable-restored.json`, and
`acer-controller-motion-*-summary.json`.

## Runtime conclusion and remaining work

Keep the working Windows source. Its 100 Hz moving delivery is verified; it is not a universal
hardware ceiling. Raw controller gyro, its physical resolution, scale and motion update rate remain
unverified in these tested firmware states. A 125 Hz gamepad packet rate is not a 125 Hz gyro
result. No production backend, capability or HardwareVerified claim was widened. Positive-axis
trials and angle/scale validation still remain; the all-axis movement captures do not replace
separately named positive rotations.
