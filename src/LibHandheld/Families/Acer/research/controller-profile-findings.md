# PredatorSense managed contracts, 2026-10-27

Offline research only. No target assembly was loaded or executed, no Acer service connection was
made, and no hardware was opened. ILSpy 11.0.0.9375 supplied readable reconstruction; REA MCP 6.8.0
supplied independent PE/CLI identity and CIL anchors. Proprietary binaries, decompilation and raw
Evidence remain private local artifacts and are not included in this repository.

## Exact artifacts and REA evidence

- Source ZIP: `Predator Sense_Acer_1.0.1563.0_W11x64_A.zip`, SHA-256
  `7bd158621b6e0135a046ecddb3df9e8b6fca0cc13b331046a4fa565a09784263`, extracted through REA MCP.
- Inner bundle: `UWP/Desktop/7bd2a3c9c08b457ea173692ad3e17700.msixbundle`.
- Inner package: `Handheld.Desktop.Package_1.0.1563.0_x64.msix`.
- `Handheld.Desktop/AcerGamePad.Core.dll`: SHA-256
  `41392f81a12d84498bcb1c1cec7c6d81a23718252b8473b96bfb6e7a2253370d`, MVID
  `371523e2-0fd8-4c41-9f36-f4594019038c`. This matches the prior checked-in research exactly.
- `Handheld.Desktop/Handheld.Desktop.dll`: SHA-256
  `38364019c91627c81c4dafd1be34694b5a4a2a4ba6a42fdd1ffceb5050091c2c`, MVID
  `939fd990-0c3e-494e-af1a-a20c3b4cc768`.
- Core identity Evidence: `ev_2fdb75aeda7f4f46e75a189d43638ca3db39d9d324c3719b8250766dea3592c4`;
  summary `ev_2ec3ccc9725abb4a0ace26c03bb99c7e6214ca843ec08a2fb7653c35778b0491`.
- Initial Core selected members:
  `ev_9823012d853a498b6f7939b3682e8eff9091c6b222f49a33058ea37317f97ae0`; summary
  `ev_6c6011e8713010f6010cad12c89cfe322a91c87d00b19a02568513308b826615`.
- Expanded Core contracts and complete CIL anchors:
  `ev_507d1693bdd167f448584d6c2418cff6e82053ca51ba53517b4aa532a28e91cf`.
- Selected desktop contracts plus whole-assembly MemberRefs:
  `ev_94b3385547653d1f0f178f408cfca2fbc3f7785078c880b051c2065127fa0dbb`.

These Evidence IDs identify the retained local analysis session, not public downloads. In the WSGM
integration checkout, the raw records are under ignored
`artifacts/acer-atlas8-rea-20261027/managed/`. Binary hashes and member tokens above identify the
exact shipped inputs independently of those local paths.

## New finding: raw gyro upload and firmware gyro mapping are separate

`DeviceProxyService.SetMotionStatus`, token `0x0600004c`, writes immediate command `0x2f` with 1/0.
It has no status query, saved previous-state field, or acknowledgement wait. `ReceiveData`, token
`0x06000053`, decodes command `0x30` into six int16 values at 5/7/9/11/13/15 and adds 32768. Its
decoded MotionData model carries only six integers, with no physical scale, timestamp or counter.

The complete shipped desktop assembly MemberRef collection has 2,640 entries and no references named
SetMotionStatus, OnMotionDataUpdated or MotionData. A finite search of all decompiled desktop C#
also found no such names. This establishes no direct compiled consumer in that desktop assembly, not
absence of reflection, other packages or firmware behavior. There is therefore no desktop raw-stream
start/stop policy, rate or physical conversion to copy from these inspected callers.

The UI's `ControlModeHelper.SetGyroProfile(int,string,double,double)`, token `0x06000da5`, instead
edits `Profile.SensorModule`:

- None: MotionEnable=false.
- Left stick: MotionEnable=true, MapSelection=0, IsOutputAsMouse=false.
- Right stick: MotionEnable=true, MapSelection=1, IsOutputAsMouse=false.
- Mouse: MotionEnable=true, MapSelection=0, IsOutputAsMouse=true.
- Always: SensorSwitch=true. A selected enable button instead sets SensorSwitch=false and
  QuickKeyName.
- PressToToggle=true, PressToEnable=false, PressToDisable=false, Sensitivity=4.
- OriginalData1=(byte)(UI deadzone/100*254), TargetData1=(byte)(UI sensitivity/100*254),
  OriginalData2=255, TargetData2=255.

It then calls `Proxy.SaveProfile()`. Core SaveProfile (`0x06000047`) calls PreviewProfile and
SyncToROM (`0x06000049`, command0x22). These firmware gyro mapping settings are persisted; they are
not physical-unit scale or raw-upload calibration. A future raw motion collector must not overwrite
them merely to enable upload. Whether firmware mapping and raw upload coexist on the unit remains
untested.

`SensorModule` is a 10-byte profile segment. First-byte flags are motion-enable bit0, press-enable
bit1, output-as-mouse bit2, flipX bit3, flipY bit4, disableX bit5, disableY bit6, toggle bit7.
Remaining bytes are sensor switch, quick key, sensitivity, anti-deadzone, OriginalData1,
TargetData1, OriginalData2, TargetData2, MapSelection. These are firmware mapping fields, not gyro
measurement units.

## New finding: output packet builders disagree with their enum

`DeviceProxyService.GetWriteProfileCommand`, token `0x06000055`, CIL file offset114308, emits
64-byte packets with:

`0f 00 00 3c 21 profile offsetHigh offsetLow chunkLength <up to55 bytes>`.

REA observes `ldc.i4.s 33` at IL+85 after index4 at IL+84. The CommandType enum nevertheless
declares WriteProfile=3. The actual RAM-write path uses `0x21`, so blindly using that enum's
WriteProfile value would implement a different command.

Profile offsets are big endian in this outer packet (high then low); this does not change the
separate EC protocol's little-endian fields. Core profile reads use command0x04 with the same
profile/offset/length header; read replies use0x05. Complete profile length comes from ProfileInfo,
not the packet size.

`DeviceMessage` builds only `0f 00 00 3c command payload...`, with variable length.
`DeviceInfo.Write`, token `0x060000b8`, passes it directly to HidStream.Write. There is no explicit
padding in those inspected wrappers; profile RAM writes are explicitly64 bytes. Descriptor/driver
framing remains a hardware fact.

`ProfilePacketInfo` checks length64 and dispatches on byte4, without validating the prefix
bytes0..3. Do not turn that permissive vendor parser into a verified frame contract.

## New finding: command ACK and retry policy must not be copied

Core `CommandQueue.Instance_OnReceiveMessage`, token `0x0600029d`, CIL file offset136744, marks the
queue head acknowledged for any received data whose byte4 is not F4, 0A or27. It performs no
request/command/offset correlation. Motion0x30 is not excluded. An incoming sample could therefore
satisfy the vendor queue's software ACK predicate; this is not proof the pending command succeeded.

`CommandQueue.Push(DeviceInfo,DeviceMessage)`, token `0x060002a1`, immediately calls Device.Write
for IsImmediate commands. Raw motion enable, mode switches and RGB play-state commands bypass the
acknowledged queue and its retries.

`ProcessQueue`, token `0x0600029c`, retries a queued command after1000ms up to a counter of5, then
sends immediate ResetDevice0x28 and returns the original to Waiting. The inspected reset branch does
not clear that retry counter. These are vendor behaviors to document, not a safe LibHandheld
retry/recovery contract. Transport completion, protocol acknowledgement and observed hardware effect
must remain distinct.

## New finding: controller RGB preview is a RAM path

Desktop `LightingEffectHelper.ApplyToDevice`, token `0x06000df3`, calls `Proxy.PreviewProfile()`
only. Brightness, speed and color setters update Animation fields and reach that RAM preview path.
They do not call SaveProfile/SyncToROM in that helper. `DeviceProxyService.SaveProfile()` explicitly
adds SyncToROM; its separation is real.

This provides a source-grounded volatile preview mechanism using bounded changed profile ranges. It
is still a profile write and preserves the whole profile model; it is not evidence of a standalone
universally safe RGB packet. It is distinct from the package's AcerLightingService and Ultron
lighting transports.

## Calibration lifecycle

Desktop `GyroscopeCalibrationPage` uses SetCalibrationState0xFD with modes Enter0, Complete1,
Cancel2, GetStatus3. It polls status on a50ms DispatcherTimer, completes on CalibrationInfo.Motion
and sends Cancel when navigating away while calibrating. It never consumes MotionData or computes
physical scales. The50ms value is status polling, not sensor sample period. These are explicit
calibration operations and must not be run during ordinary input acquisition.

## Remaining evidence limits

Gyro physical scale/full scale, axes, sampling cadence, bias, raw-upload ownership restoration and
coexistence are unresolved. No independent model-specific managed fan-topology table was found; the
two displayed fan rows are not hardware discovery. Native fan discovery is documented in
[the power/fan protocol](../POWER_FAN_PROTOCOL.md) and [native findings](power-fan-agent-native.md).
This report adds protocol evidence only; it implements or validates no runtime device support.
