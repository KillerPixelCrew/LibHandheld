# Acer Predator Atlas 8

## Controller firmware 1.05, 2026-11-08

The "BIOS 1.05" update Predator Sense offered is controller firmware. On the maintainer's PA08-I51
(BIOS V1.04 dated 2026-09-08, EC 1.2, both unchanged), Predator Sense 1.0.1563.0 flashed the
controller over HID DFU at 03:12. Its `core-20261108.log` shows `acer_fw_1.05.ufw`, image
`pid: DG_3315T, vid: 1.05, UfwChecksum: 5CB2-0787E5BE`, device before `DG_3315T, vid: 1.00`, then
`upgrade success`. A 2026-11-07 20:47 WSGM start on the same BIOS still owned the mode lane, so the
release was 0100 until this flash. The remote tester, whose BIOS also stayed 1.04, matches.

After the flash the USB device is still 0502:F901 in XInput mode, now release 0105 (`REV_0105` on
the composite, MI_00..02 and every MI_01/MI_02 collection). The interfaces and every collection shape
are unchanged from 0100: MI_00 Xbox 360 (`xusb22`) with IG_00 gamepad 0001:0005, input 15;
MI_01 FFA0:0001 in/out 64 and FFF1:00F5 in 15; MI_02 keyboard 0001:0006, mouse 0001:0002 in 8,
consumer 000C:0001 in 5, QAM 0088:0001 in 3 and FF5A:0001. WSGM's start then logged
`acer-controller-mode=Passive(PrerequisiteMissing)` and `acer-lighting=Passive` because the lane
admitted 0100 only. The rear/QAM button source refused the same collections, so the rear keys stayed
in Predator Sense's Macro mode and neither the back buttons nor the QAM key reached the virtual
pad. The virtual Steam Deck pad itself was created.

An attended gyro trial on 1.05 (WSGM stopped) repeated the 2026-10-27 method. In XInput the
command lane answered mode query 26 (`01 00 00 01 00`), read SensorModule `E0 01 00 04 00 19 32 FF FF
00`, accepted MotionEnable bit 0 with readback, and took 2F 01/2F 00. Switching with 24 02 00
re-enumerated F90A release 0105 with the 0100 DInput shapes: gamepad 0001:0005 in 64/out 32/feature
48, vendor FFF0:0040 in/out/feature 64, keyboard 0001:0006 in 9/out 2, mouse 0001:0002 in 8,
consumer 000C:0001 in 5. Its command lane read and gated the module the same way. Both modules,
then the original mode, were restored and read back exactly.

Gyro result: no raw controller gyro on 1.05 either. FFA0/FFF0 delivered no 0x30 motion packets
in either mode during a 5 s rest and 15 s rotation (Windows reference peaks 250/161/107 and
381/193/134 degrees/s). The DInput report ran at 125 Hz and only left-stick bytes 1 and 2 changed;
offsets 13..24 stayed zero with the fixed 0x10 at 22. The XInput HID gamepad (IG_00, release 0000)
changed only stick bytes. The Windows LSM6DSO source remains the motion source. Evidence: ignored
`artifacts/acer-fw105-gyro/` (trial log, results.json, raw report and Windows sample captures) and
the probe in `artifacts/acer-fw105-probe/`.

Change: release 0105 is admitted with the exact 0100 shapes in both topologies: the command lane
(mode and lighting) on F901 FFA0:0001 and F90A FFF0:0040, FFF1:00F5 15-byte rear input, 0088:0001
QAM input, the F90A gamepad button source, DInput companion and DInput rumble shape. Remaining: an
attended WSGM run on 1.05 confirming that the managed XInput/Combination pair is applied and
restored, lighting reads its 883 bytes, rear keys produce report 9A, and DInput rumble is felt.

## BIOS delivery sources and BIOS 1.05, 2026-11-08

Read-only research into whether Atlas 8 BIOS 1.05 changes the controller; nothing was run or flashed.
Predator Sense 1.0.1563.0 Live Update asks its local XSense service (`localhost:15152`,
`driver/getUpdateList`), which is `AcerService.exe` in the Predator Sense package, a bundled Node
service. It fetches `GET https://backend-prd-imub2p4wyq-uc.a.run.app/xsense/api/getDriverList3/{model}/{serial}`
(or `getDriverList2/{model}`) with an HS256 bearer token built from a constant in the service. The model
is SMBIOS `SystemProductName` (`Predator PA08-I51`). The response is `{"driver":[...],"bios":[...]}`
with `global-download.acer.com` file paths, and a BIOS item is shown only when it is an INF capsule
package. On 2026-11-08 the backend returned 31 drivers and an empty BIOS list for the PA08-I51 without
a real serial, so a serial- or version-targeted rollout cannot be excluded.

Acer's website (`POST https://www.acer.com/us-en/DynamicContent/GetDriversAndManuals`,
`{"ModelName":"Predator PA08-I51","Local":"en-us"}`) lists only BIOS 1.04 (2026-10-30, "Updates Insyde
patches"); only the us-en site has the model. Winhanced reads that same website response through a
browser and downloads from `global-download.acer.com`, so it offers nothing beyond it. A 1.05 file
cannot be fetched without its per-file `acerid`, and the Microsoft Update Catalog lists no Atlas BIOS
capsule or the 1.0.1000.4 component. The 1.04 package is an Insyde wrapper (`F7V_V1_04.exe`) whose
packed payload was not decoded; no controller firmware identity was found in it.

Result: BIOS 1.05 was not obtainable, so its effect on the controller's USB product ID, release 0100
or default mode is unknown, and no change to the release-0100 admission is proposed. Next evidence: the
serial of a unit that was offered 1.05, or a tester's wsgm.log with the `acer-controller-mode`
collection line. Local responses and hashes are in ignored `artifacts/acer-bios/`.

## IPF session across Modern Standby, 2026-11-08

The maintainer's log (build 3.0.2.2012) showed IPF lost after a wake. `AcerPowerService` closed the IPF
session on suspend and reconnected on resume. `IntelIpfTransport.Connect` blocks for up to 10 s waiting
for the server's AppCreate callback; after the wake the callback never came. That wait outran the 5 s
resume deadline (TaskCanceled, "starting a fresh cycle"), and when it expired the fallback selected
firmware and disabled and stopped `ipfsvc`. The same reconnect failed again across the next standby.

The session now stays open across a standby, since IPF signals a connected client itself (AppSuspend,
AppResume). Acquisition no longer opens it: a dead or missing session is opened by the next limit write
before anything is dispatched, a failed open refuses that write, and the path never leaves IPF or stops
`ipfsvc`. A refused write closes the session so the following one starts fresh. The write path logs what
IPF or MSR 0x610 reports afterwards. Why the server withholds AppCreate after a standby was not
established, and survival of a connected session across standby awaits an attended run.

The next log (build 3.0.3.2015) showed the kept session alive but refused after a wake: the first write
failed with ESIF error 0x3EC, `ESIF_E_INVALID_HANDLE`. A reconnect then again never got AppCreate. Intel's
archived source (github.com/intel/dptf) gives one explanation for each. ESIF hands out a new domain handle
each time it creates a domain (`esif_uf_app.c`), and the transport kept the first one it saw, so it now
follows ParticipantCreate/Destroy and DomainCreate/Destroy. And `ipfsrv_iface_cli.c` states that an app name
must be unique or the connection is terminated; every connection used "WSGM" and a closing session stops
asynchronously, so each connection now has its own name. A first request refused as invalid applied
nothing, so it is the one write sent again, through a new session. The transport logs AppSuspend, AppResume
and handle changes, and a failed connect says whether IPF asked the name and whether the session closed.
This is the Linux archive, not the Windows 2.3 build, and no attended run has confirmed either cause yet.
The controller re-enumerates just after a wake, so a handle opened moments earlier fails
`HidD_FlushQueue` with error 1167. Before anything is sent, the command lane now reopens the same
collection on its physical port, for up to 3 s, instead of faulting lighting for the session.

## Focus Zoom disabled by maintainer decision, 2026-11-08

This supersedes the earlier same-day AFZsvc exemption below. The maintainer explicitly chose to
disable Focus Zoom despite the tester's reported controller dependency. The exact PA08-I51 OEM
takeover again includes `AFZsvc`, optionally `FocusZoomUserService` if installed as a service, and
the `FocusZoom`/`FocusZoomUserService` process names. Setup's unblocked-package recovery still
allows Predator Sense's desktop app for BIOS delivery, but never restores service records. The
existing accepted takeover persists across exit and Device Integration off; uninstall restores
recorded start types. No driver package or binary is removed.

Attended read-only evidence on the maintainer's PA08-I51, PTL F900_PTG:

- AFZsvc was Automatic/Running, PID 4424, under
  `focuszoomservice.inf_amd64_88fac5f6103aea02`. Its INF declares DriverVer
  `07/17/2026,1.0.1000.3`, software component
  `SWC\VEN_1025&DEV_165F&11&SWComp_AcerFocusZoomDriver` and `AddService=AFZsvc`.
- Process ancestry was AFZsvc -> FocusZoomUserService (PID 13068) -> FocusZoomUserService
  `launch` (13076) -> FocusZoom (13448). No FocusZoomUserService Windows service/template, Focus
  Zoom Run entry or scheduled task was installed. Missing optional services are skipped.
- SHA256: FocusZoomService.exe
  `2CACBFDFAAA870B9956BD518D0A8270153D39DC649D8DB06581DDF00BD588DD8`,
  FocusZoomUserService.exe `FC538AE8DE7172DF9677F830771FDD3497FE5CEE27D265770BA4EDDDC90431BE`,
  FocusZoom.exe `DB4D8FD67D143A6B681BECBBB7761310D0136B4DD978E217DFE9EAE47FE0B1EF`.
- Static Ghidra 12.1.4 inspection of FocusZoom.exe: `0x1400171f0` opens the first HID with usage
  page FFF1, read/write and shared, without a VID/PID filter. `0x140017fd0` performs blocking
  ReadFile, interprets 9A zoom events and 9B stick reports, and has 500 ms open-failure / 1500 ms
  read-failure backoff. It drives Windows.Graphics.Capture and D3D11. That is controller-related
  input, but does not establish that the base controller requires the service.
- A 20 s snapshot measured 0 CPU seconds for all four processes, 0% sampled GPU engine use and
  zero sampled I/O. A longer 10 s sampler caught FocusZoom.exe at 1.15625 CPU seconds in
  10.3513 s. This is activity, not a reproduced extreme-power fault or a demonstrated busy loop.
- SRUM export attributed raw CPU-energy sums of 720267 to FocusZoomUserService (Nov 6-8),
  153565 to FocusZoom.exe (Nov 8 14:35-15:29) and 2356 to FocusZoomService (same hour).
  These are unconverted SRUM values with different aggregation windows, not watts. SleepStudy
  also lists the user service. No Focus Zoom event provider/channel was registered; Application
  Error 1000 records a FocusZoom.exe fail-fast crash at 10:42:13, exception C0000409, offset 28CED.
  Text logging is conditional on `HKLM\SOFTWARE\OEM\FocusZoom\Log`; that key was absent.

Live disable recorded AFZsvc's original start type 2 in WSGM's takeover journal before changing
SCM, then stopped/disabled it and ended the authorized Focus Zoom children. The driver package
remains installed. XInputGetState from an unallowlisted diagnostic process returned 1167 for every
slot while WSGM's HidHide cloak was active, so this does not prove a controller failure or pass.
Attended button acceptance and uninstall recovery remain to be exercised. Raw local evidence is
under WSGM `artifacts/focuszoom-2026-11-08`; it is not library build input.

## AFZsvc left startable, 2026-11-08 (superseded)

A remote tester's Atlas 8 received "Acer Incorporated Software Component Driver Update (1.0.1000.4)"
through Windows Update. Afterwards a process named "Focus Zoom" ran with high CPU, and the built-in
controller stopped working whenever that service was disabled, which WSGM did. Re-enabling the Acer
services and starting Predator Sense was the only recovery. `AcerModels.ManagerApps` therefore no
longer names `AFZsvc`; the other nine services, three tasks, widget package and keyboard leaf stay.

Evidence and inference: the package version matches none of the supplied archives (Acer APP Base
1.0.0.6, DES 1.0.0.3020, PredatorService 1.3.3000.18), and neither the Microsoft Update Catalog nor
public sources list it, so its INF, AddService entries and binaries are not inspected. `AFZsvc` as
the host of "Focus Zoom" is inferred from the name only. The 2026-10-27 set was chosen to turn all
Acer software off, not as the Win+G cause, so AFZsvc was never shown to be part of the Win+G path.
The decompiled Predator Sense gamepad core defines controller modes Offline 0, XInput 1, DInput 2,
Special 3, Desktop 4, BIOS 5 and TESTING 6, and its desktop app switches them; LibHandheld's mode
lane opens only for 0502:F901 or F90A at release 0100. When it cannot open, the mode service now
logs every present 0502 collection with product, release and usage, so a tester's wsgm.log shows
whether a controller firmware or topology change is involved.

The "XBOX Synthetic Gamepad Bus" warning the tester saw is Microsoft's GameInput VHF source device
`SWD\SYNTHETICGAMEPADENUM\SYNTHETICGAMEPAD0`. It fails with Code 10 while HidHide's HIDClass upper
filter is installed (HidHide issue 223, open since 2026-11-03), independent of WSGM's virtual pad,
which does not use it. Remaining: the tester's wsgm.log, the identity of the component that hosts
Focus Zoom, and an attended Atlas run with that component installed.

## Predator Sense for Handheld can be started, 2026-11-07

Acer has started delivering Atlas 8 BIOS updates through Predator Sense for Handheld and does not
post them separately on its support site. So that the user can open the app and receive them,
`AcerModels.ManagerApps` no longer names the desktop process `Handheld.Desktop` or its MSIX family
`ULICTekInc.PredatorSenseforHandHeld_nt9dgb7efx6bt`. WSGM therefore no longer sets that package's
status to Disabled and no longer asks the desktop window to close. Everything else stays as
measured: the ten services, the three tasks including `PredatorHandheldLauncher`, the widget package
and `Handheld.Widget`, and the dedicated controller keyboard leaf.

The decompiled Predator Sense 1.0.1563.0 desktop app (local
`artifacts/acer-atlas8-rea-20261027/managed`) runs Live Update from its Update page. It asks local
HTTPS services at `localhost:15152` and `localhost:54433` for the update list and the
download/install sequence, and its items carry an `IsBios` flag. While the Acer services stay
disabled, those requests may fail; which service listens on each port was not traced. Neither an app
start nor a BIOS delivery with WSGM installed has been recorded yet.

## Native percentage charging and rear-logo lighting, 2026-11-07

The attended unit was reached through the maintainer's paired Orca runtime. Identity was Acer
`Predator PA08-I51`, PTL / `F900_PTG`; EC data version was `0800`. No Acer OEM service was started.

The percentage-control lead came from static inspection of Winhanced Installer 1.0.0.5, SHA-256
`CAB494FA1FEE17BED27839D128A4B4CC17BB28104ECDCB14C71D44F6E2898A7B`, specifically
`HardwareControl.dll`'s `AcerChargeLimit` and `AcerEcProtocol`. LibHandheld's implementation was
written independently from the packet contract and the live observations below.

- EC command `0B03`, subcommand 2, selector 3 queries the native boundary. Subcommand 1 writes
  enabled, upper and lower at payload offsets 6, 7 and 8, excluding report ID A0. A single 80/50
  write was acknowledged and queried back as `00 E0 03 0B 00 03 01 50 32`. This establishes native
  percentage configuration/readback; charging held at the threshold has not been measured.
- The previously uninitialized query returned FF in the state/threshold fields. After the bounded
  test, the native boundary was disabled and queried as `00 E0 03 0B 00 03 00 FF FF`. Disabled
  firmware clears the thresholds; it does not retain the 100/50 values sent with disable.
- WMI optimized charging was initially enabled. Its existing mode was restored with an accepted
  setter and verified status/function bytes `00 00` / `01 00 00 00 00` after the trial.
- The runtime keeps optimized charging and a custom ceiling as separate controls. Selecting a
  percentage disables the optimized mode; enabling optimized charging disables the custom ceiling.
  The custom control offers 51..100%, retaining the tested 50% resume threshold; 100 disables it.
  Both known originals are journaled and restored. Missing readback remains unknown and does not
  prohibit a write. An original that cannot be captured is reported as unverified restoration.

The rear logo is a separate I2C HID endpoint: ENE `0CF2:5130`, release 0001, `FF5A:0001`, with
11-byte feature reports. A1 returned `A1 01 80`, listing target 80. A2 selects the target for A3;
the capability response was `A3 80 01 05 01 3B 00 00 00 00 00`: one LED, with off, static and
breathing modes among its advertised effects. The ordinary USB HID path filter cannot enumerate this
I2C path; the service selects it by exact HID attributes from the complete collection list.

The initial Winhanced-style A4 command (mode 1, zero mask) produced only a brief white flash, as
reported by the maintainer. It is not the implemented color protocol. Acer's native lightbar encoder
was inspected alongside
[ENE protocol observations](https://github.com/shwetankg07/kbrgb/blob/main/PROTOCOL.md) and the
[descriptor-backed mode/mask correction](https://github.com/cleyton1986/predator-sense/issues/31).
These are protocol references, not copied implementation code. The final word is a 16-bit LED mask,
not a duration. Mode 1 is off, mode 2 steady color and mode 4 breathing; mask 1 addresses the logo.

A corrected red/blue/off trial sent `A4 80 02 32 00 00 FF 00 00 01 00`,
`A4 80 02 32 00 00 00 00 FF 01 00`, then `A4 80 01 00 00 00 00 00 00 01 00`. Every feature write was
accepted. With WSGM running, the maintainer still reported no steady colors. After WSGM exited
through its normal update-exit event, the maintainer observed blue and the final deliberate off
state. After the host routing fix below was deployed, the maintainer selected red through WSGM and
confirmed that it stayed on with WSGM running. This establishes normal application color control and
blue/off on the separate endpoint; animation/speed remain separately unmeasured. A4 and A2 are
write-only; GetFeature acceptance on them does not establish lighting-state readback. The runtime
therefore publishes the logo's initial values as unknown, uses only its declared effects, writes a
complete report on an explicit control change, and leaves the chosen lighting on release. No ring
profile, ninth controller slot, controller mode or ROM is changed by this service.

The real LibHandheld battery and logo services were acquired on the unit without changing any
setting: both reached Owned. The battery published optimized charging enabled and no custom ceiling;
the logo published its four instance `logo` controls with unknown initial values. Normal application
color control was subsequently accepted as described above; animation/speed and charging behavior
remain distinct from those observations.

Adding a named logo instance also exposed a host routing defect: unqualified role-based ring
brightness/effect setters became ambiguous. WSGM sends lighting through the exact capability ID and
instance instead of reselecting the control by role. This routing fix does not establish which
active hardware operation interfered with the raw logo trial. The running application's log then
recorded Applied for shared ring brightness/effect and `lighting.zone-color#logo`, matching the
maintainer's red-light observation. No periodic logo reapply or power-mode workaround was added.

The requested on-device static vendor inspection matched installed `AcerHIDLightbarController.dll`,
SHA-256 `8C498523D7AAF8C2804583E2EC7FBFAA68ADB76F9D38AC0B3B9403FD53906099`. Its A4 writer is at
image VA `180011650`; the logo's per-LED setter supplies mask 1, while the general mode setter
supplies mask 0. The successful A4 path has no extra enable/select call. Earlier cached writer
`18000eb40` belongs to `AcerHIDStickController.dll`, not this lightbar binary. The cached desktop
UI's separate logo-beacon mode remains a qualified ownership lead, not an established firmware side
effect or a justification for additional WMI/HID writes. The single mode-0 reselect trial kept mode
0 unchanged, but had no reliable visual timing report and establishes no reset causality.

[OEM input and software control](OEM_INPUT_AND_DISABLE.md) is the complete operational reference:
physical source/report mapping, rear-mode acquisition/restoration, software disable targets,
post-reboot evidence, persistent package activation blocking and recovery.

[Power-limit tests and runtime bounds](POWER_LIMIT_TESTS.md) distinguishes advertised limits,
accepted profile values and measured load power, including the unresolved sustained ceiling.

[Motion tests and controller modes](MOTION_TESTS.md) records Windows cadence/resolution evidence,
DInput and controller-off topology, and the limits of the tested raw gyro paths.

## Initial physical input contribution

Source: [InputPlumber issue 759](https://github.com/ShadowBlip/InputPlumber/issues/759), reviewed
2026-10-27. This is an independently written Windows implementation of reported hardware facts, not
a port of InputPlumber application code or its temporary Linux configuration.

- [Inventory](https://github.com/ShadowBlip/InputPlumber/issues/759#issuecomment-6310419266): Acer
  system `Predator PA08-I51`, PTL board `F900_PTG`, BIOS V1.03, EC 1.2. Detection requires both
  system and board identities. Neither the marketing name nor USB identity alone selects it.
- [HID captures](https://github.com/ShadowBlip/InputPlumber/issues/759#issuecomment-6341859840): USB
  `0502:F901`, release `0004`; interface 01 contains top-level `FFF1:F5`, 16-byte reports. `9A` byte
  1 masks `02` / `04` represent M1 / M2; zero releases them. `9B` reports must not alter those
  edges. `9E 00 00 05 00` is a Guide notification without a release. The library synthesizes a 200
  ms tap using its existing OEM hold duration, releasing it on stop/disconnect.
- Interface 02 top-level `0088:0001` has three-byte QAM reports `37 81 FF` / `37 00 00`. This is a
  firmware tap delivered after the physical button is released, not a physical hold measurement. The
  reported F22 long-hold workaround is not registered as a second Guide source.
- Both OEM collections must be unique and share the same composite USB location. The regular pad and
  rumble use the existing exact-VID/PID XInput service. No calibration, mode switch, firmware
  profile write or speculative controller command is sent on acquisition.

The reporter found that consuming vendor input prevents Linux controller freezes. This is evidence
for retaining the raw reader, not proof of the same behavior or a required workaround on Windows.
Rear reports depend on the controller's M-key mode. The runtime now owns the temporary mode through
the controller lifecycle as described in the attended follow-up below; it does not rewrite profile
mappings.

## Boundaries and acceptance

The initial contribution supplied exact detection, standard controller/rumble routing, OEM button
decoding and stock gamepad glyphs. The on-device implementation below adds EC power, fan control and
RPM, battery protection, Windows motion and volatile controller lighting. Optional Intel processor
settings remain owned by the shared processor service and use the signed PawnIO IntelMSR module.

Predator Sense 1.0.1563.0 was supplied separately by the maintainer for offline protocol research.
The follow-up [power and fan protocol research](POWER_FAN_PROTOCOL.md) records the separate EC HID
power path, WMI fan commands, field layouts and remaining recovery/acceptance work. Its proprietary
binaries and decompiled source are not included in the library. Protocol findings are research
inputs; motion units/orientation, native controls and restoration need their own reviewed
implementation and measured acceptance.

## PA08-I51 on-device implementation, 2026-10-27

The maintainer authorized autonomous hardware work. The unit reports system Acer / Predator
PA08-I51, board PTL / F900_PTG and BIOS V1.03. Its controller is 0502:F901 release 0100, with a
15-byte FFF1:00F5 input collection and three-byte 0088:0001 QAM collection. The original published
0004 / 16-byte variant remains supported. Both are exact report shapes; other releases are refused.

Implemented controls and evidence:

| Area     | Mechanism and observed result                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Power    | I2C HID 1025:174B, FF05:0001, 65-byte feature reports, ID A0; EC data version 0800. Firmware advertises PL1 10..40 W and PL2 maximum 65 W. Both 20/40 W and unified 20/20 W Manual commands read back correctly. Release restored the original 25/45 W manual profile and active Turbo mode. Uninterpreted profile fields were retained.                                                                                                                                                                                                                                                                                                                      |
| Fans     | Health mask 02CD discovers SYS and SYS2. AcerGamingFunction WMI supplies Auto, Max and fixed-speed commands. EC health indices 4 and 8 supply the two RPM readings; index 1 supplies CPU temperature (53 degrees C in the final lifecycle trial). A bounded Max trial increased readings from 2829/2807 to 3281/3237 RPM. Release sent Auto. Behavior getters returned zero, so original behavior was unknown and restoration was reported unverified.                                                                                                                                                                                                        |
| Battery  | BatteryControl.GetBatteryHealthControlStatus and SetBatteryHealthControl, battery 1, function mask 1. Optimized charging toggled from disabled to enabled, read back enabled, then restored disabled with a clear journal. Calibration and smart-charge schedules are not invoked. No arbitrary percentage ceiling or bypass is claimed.                                                                                                                                                                                                                                                                                                                      |
| Motion   | Windows exposes both gyrometer and accelerometer through WinRT and the legacy Sensor API. Fresh samples reached canonical controller frames. The standard landscape Windows frame maps to application (X, Z, -Y); physical positive-axis trials remain outstanding.                                                                                                                                                                                                                                                                                                                                                                                           |
| Lighting | Controller FFA0:0001, 64-byte reports. Output ID 0F requests profile 0; input ID 10 returns RAM profile 1. Only the 883-byte lighting section at offset 586 is addressable. Brightness, speed, solid/breathing effects and ring colors use RAM command 21. The eight visible segments are mapped below; the ninth wire slot is retained. Brightness 50 -> 45, both effects, color 123456 and speed 10 read back correctly; release restored every lighting byte. The dedicated command input queue is flushed before snapshots to discard stale replies. No mapping or ROM command is sent. Lighting acquisition currently admits measured release 0100 only. |
| Lifetime | Physical controller and motion samples, four OEM descriptors, controller disable/re-enable, library suspend/resume and clean stop were observed. A bounded 10% rumble command was followed by an explicit zero; no human perception is claimed.                                                                                                                                                                                                                                                                                                                                                                                                               |

## Attended RGB slot mapping, 2026-10-27

The maintainer observed a retained-profile RAM preview with one static keyframe at 60% brightness.
Its nine RGB slots were red, green, blue, yellow, cyan, magenta, orange, violet and white, in order.
The original lighting section was journaled before dispatch; readback matched the preview bytes. The
preview has a ten-minute limit and restores the original lighting section afterward.

With the screen facing the user, the maintainer identified the left stick ring using the palette,
then confirmed each of the eight segments individually with white while all other slots were dark:

| Physical position | One-based RGB slot | Zero-based RGB index | Observed color  |
| ----------------- | ------------------ | -------------------- | --------------- |
| Top left          | 7                  | 6                    | Orange, FF8000  |
| Top right         | 6                  | 5                    | Magenta, FF00FF |
| Bottom left       | 8                  | 7                    | Violet, 8000FF  |
| Bottom right      | 5                  | 4                    | Cyan, 00FFFF    |

The right ring was then mapped one white slot at a time:

| Physical position | One-based RGB slot | Zero-based RGB index |
| ----------------- | ------------------ | -------------------- |
| Top left          | 2                  | 1                    |
| Top right         | 1                  | 0                    |
| Bottom left       | 3                  | 2                    |
| Bottom right      | 4                  | 3                    |

Only slot 9 was set to white in the final step. The maintainer reported no visible light and no
additional RGB hardware. This slot remains unassigned; no visible output does not establish its
firmware meaning. Runtime color/effect edits preserve its RGB bytes in the retained keyframes. The
tests restored the complete original lighting section with verified readback and a healthy recovery
journal. No ROM, controller mappings or calibration were changed.

The runtime exposes a shared "All ring segments" color and eight individual `lighting.color`
instances: `left.top-left`, `left.top-right`, `left.bottom-left`, `left.bottom-right`, and the
corresponding `right.*` names. Each individual command changes only its measured RGB triplet in the
active frame. Shared color affects the eight visible slots and retains the ninth. Colors,
brightness, speed and effect selection use the same native backend on both WSGM surfaces. Effects
and brightness remain shared across the rings; independent per-ring timing was not established. The
physical mapping is verified for this controller release, not every firmware.

## Attended fan control, 2026-10-27

With Acer OEM software disabled, the maintainer confirmed each physical fan separately, screen
facing the user. The EC health mask was 02CD. Native `sys` is the LEFT fan, health/RPM index 4;
`sys2` is the RIGHT fan, health/RPM index 8. The runtime now labels these Left and Right for that
measured topology; other health masks retain generic labels.

| Trial                       | Observed response                                                               | Maintainer confirmation                                                   |
| --------------------------- | ------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| Left Full Speed, right Auto | Left rose from about 2844 to 5500 RPM; right stayed about 2800 RPM              | Left fan accelerated and worked                                           |
| Right Full Speed, left Auto | Right rose past 4100 and settled around 5500 RPM; left slowed to its Auto speed | Right fan accelerated and worked                                          |
| Left fixed 50%, right Auto  | Left settled around 4100 RPM; right around 2750 RPM                             | Left was quieter than Full Speed                                          |
| Right fixed 50%, left Auto  | Right remained around 2750 RPM, close to its Auto baseline                      | Command accepted; no separate audible attribution claimed at this setting |
| Right fixed 75%, left Auto  | Right rose to about 4240 RPM; left stayed about 2775 RPM                        | Right accelerated and worked                                              |

Duty percentages are firmware control values, not a linear promise about RPM. Readings during a mode
transition briefly fell below their settled values before ramping; first snapshots are not
steady-state measurements. CPU temperature during the trial was approximately 39..45 degrees C. No
zero-duty/stop, exhaustive percentage sweep, fan curve or thermal-load acceptance is claimed.

Commands use `root\\WMI` / `AcerGamingFunction`, without running Acer services. The trial was
elevated; the non-elevated post-reboot token received Access Denied even when querying this class.
`SetGamingFanBehavior` selects one fan with its selection mask and behavior field;
`SetGamingFanSpeed` uses `(percent << 8) | selector`. The measured selectors are 2 for left and 3
for right. Auto=1, Full Speed=2, Custom=3. Native commands publish accepted values and do not
require a getter to write.

At the end, the journal restored the originals and release returned Idle with a healthy, empty
journal. Independent elevated getters then confirmed Auto for both: left `gmOutput=0x400`, right
`gmOutput=0x1000`, status 0 and decoded behavior 1. This later verified restore differs from the
initial trial's unreadable original behavior. The unknown-original Auto fallback remains necessary
and must still report unverified cleanup when an original/getter is unavailable.

## Attended rumble calibration, 2026-10-27

The maintainer completed Device Lab 0.1.2's existing rumble workflow on this same machine, with
source revision `dd6143daf330`. Report `Device Lab report 2026-10-27 1924.wsgmlab` has SHA-256
`98425C3614E5EB3778913C5A91A3DF41A0A2B6092506F22A36820D5756F151D0`. Its rumble stage is Completed;
identity was not completed, so this is continuity with the local Atlas investigation, not a fresh
board/BIOS identity attestation. The Windows.Gaming.Input route description independently names
controller `0502:F901`.

XInput slot 0 was felt. Channel 0 was identified at the left grip, channel 1 at the right grip; no
channel swap is needed. The live sliders marked the lowest felt level at 2 percent for each side.
Both sides were felt at the shortest offered pulse, 5 ms, at full strength and at the marked
2-percent strength. The first left full-strength 5 ms trial was marked not felt, followed by a
separate felt 5 ms trial. Left minimum-strength 5 ms was felt after two replays, right after one
replay. These are attended perceptual observations, not proof of hardware response below 5 ms or
guaranteed detection on every attempt. Longer pulses through 500 ms were also felt on both sides.

Windows.Gaming.Input's standard route was marked not felt in the route comparison, so this report
supports keeping the existing XInput output path. It does not establish a DInput output protocol.
The report contains 393 successful writes, no write errors, no zero failures and no remaining
vibrating routes. The live slider session reported no stop failure, and each route's final stage-end
zero succeeded. Target removal, disconnect and OS sleep/wake output cleanup remain separate
lifecycle acceptance.

The Atlas controller profile now publishes `MinimumStartIntensity = 0.02` and `MinimumPulse = 5 ms`
through the existing standard controller service. Both low/high channels remain native; trigger and
native-pulse channels remain unsupported. WSGM's existing output router applies the floor and
minimum pulse to bounded events only, preserving continuous rumble amplitudes. The shared default
60-frame/s pacing was not measured by this trial and is unchanged. Other standard controller
profiles retain their prior zero floor/pulse defaults. This calibration has not yet been exercised
through a deployed WSGM/Steam session.

Local offline inspection used Device Lab's `report` reader and the bounded JSON evidence entries
`rumble-calibration.json`, `rumble-manual.json`, `rumble-routes.json` and `rumble-writes.json`. The
37 Acer tests passed with Release compilation and no warnings. Full WSGM application compilation
stopped at the unchanged native backdrop target because `pwsh` was unavailable (exit 9009); it is
not an application build pass.

## Attended button follow-up, 2026-10-27

The maintainer completed the 27-step CLI capture on the same BIOS V1.03 / controller 0100 unit. The
front-left extra button delivered `9E0000050000000000000000000000` three times; these are Guide taps
with synthetic 200 ms release, not measured holds. The front-right extra delivered `3781FF` /
`370000` three times, a roughly 100 ms QAM pulse. Physical long holds were not tested. Face buttons,
D-pad and its up-right diagonal, shoulders, stick clicks, both full-range triggers and A+B+LB
rollover registered. View/Menu came through opposite the step names, and the left stick's leftward
travel reached only -0.133; these need an isolated confirmation before altering mapping or
calibration. The right stick reached both extrema. No reader faults or overflow occurred.

The original rear mode was XInput (1) / Macro (0), which produced no rear reports. Acer's installed
DeviceProxyService uses command 26 hex to read gamepad mode and command 24 hex to select the gamepad
and M-key modes. The response is report 10 / command 27 hex; its bytes 5 and 6 describe those two
modes. Selecting XInput (1) / Combination (1) exposes native rear reports without editing profile
mappings, calibration, lighting, other feature settings or ROM. The runtime journals the original
mode before dispatch, acquires this mode only with controller management, and restores the original
on release, disable or suspend. No user setup in Predator Sense is required.

The maintainer then completed the three-step managed rear capture. Rear LEFT delivered
`9A0400000000000000000000000000`, rear RIGHT `9A0200000000000000000000000000`, each with three
presses and `9A00...` releases. Both together delivered `9A06...`; partial release retained the
other button. Unrelated `9B00000080FF7F0080FF7F00000000` reports did not release held rear buttons.
The existing M1/bit 02 and M2/bit 04 protocol labels remain; measured physical left maps to M2,
right to M1. There were no reader faults or dropped events. Mode acquisition was Owned and release
Idle with verified restoration to the original XInput/Macro mode. This proves rear acquisition and
release on this unit, independently of the normal user-configured profile.

### Front shortcuts and device attribution

The subsequent six-step front capture showed the left short Guide HID report followed about 200 ms
later by injected LeftWin+G. Left long presses produced a complete, non-injected LeftWin-down /
Tab-down / LeftWin-up / Tab-up sequence, with no Guide HID notification. Right holds produced
non-injected F22 down/repeat/up; these differ from the shorter QAM HID taps. The keyboard hook alone
cannot identify a physical source or distinguish this clean Win+Tab from the same keys on another
keyboard.

With the maintainer's direction, the Acer user-mode software stack was stopped and its restart
entries disabled for comparison. The maintainer confirmed that Win+G and the Predator Sense launch
disappeared, while Win+Tab persisted. Native EC power, fans, battery, lighting, controller, motion
and OEM services still acquired and stopped cleanly with those applications absent.

The four-step Raw Input capture then attributed both Win+Tab and F22 to the controller's USB
keyboard collection `HID\\VID_0502&PID_F901&MI_02&Col01`, serviced by `kbdhid`, usage 0001:0006. The
volume buttons instead used the separate I2C consumer collection `HID\\1025174B&Col08`, with reports
`33E900` / `33EA00` and neutral `330000`. No events were dropped and no reader fault was reported.
This supports targeting the USB keyboard collection without targeting the parent controller, the QAM
vendor collection or the I2C volume collection.

The maintainer authorized disabling that exact keyboard collection. Normal disable was refused as a
required system device; PnPUtil `/disable-device /force` accepted it but required a restart. After
the requested restart, Windows reported problem code 22 (disabled) for that exact keyboard leaf. The
maintainer confirmed that holding the left OEM button no longer opened Task View or sent Win+Tab.
The other controller HID collections, XInput interface and I2C volume collection still enumerated
healthy.

The maintainer then completed the post-reboot front-tap/volume capture. Three quick front-left
presses each delivered the Guide HID notification and its bounded synthetic release. Three quick
front-right presses each delivered `3781FF` / `370000` and matching QAM edges. No injected Win+G was
observed during those steps. Both volume buttons were exercised three times and delivered their
respective `33E900` / `33EA00` consumer reports followed by neutral `330000`, plus the corresponding
Windows volume-key edges. There were no reader faults or dropped events. These observations verify
that short front OEM input and volume input survive the exact keyboard disable; they do not
establish new acceptance for unexercised controls or live WSGM/Steam routing. Disabling this
keyboard also removes its F22 hold signal; do not claim native right-long support while that lane is
disabled. This device differs from the Claw's shared firmware PS/2 keyboard: its volume buttons have
an independently attributed I2C source, so disabling i8042prt or kbdhid globally, the composite USB
parent, or the I2C device is unnecessary and outside this action.

The nine recorded Acer service startup settings remained Disabled and the services Stopped after
reboot. Handheld.Widget nevertheless reappeared in the user session, so service/task changes alone
do not prove every OEM application stays absent across sign-in. FSE startup was disabled through
HKCU GamingConfiguration / StartupToGamingHome = 0 and verified again after restart; Explorer
remained the configured shell. The original service/task/FSE states and the exact keyboard-enable
command were saved locally before the changes.

The follow-up disabled the remaining UEIP service/tasks and both exact Predator Sense MSIX packages
using the elevated PackageManager Disabled flag. Explicit desktop/widget activation attempts then
returned 80073D5A with PID 0, both packages reported Disabled/NotAvailable, and no Acer software
process remained. An opt-in local maintenance task rechecks the closed target set at sign-in and
every five minutes, including replacement versions of those package identities. Its first run
completed successfully; it is not an application/runtime feature. The operational reference above
lists every target, proof limit and restoration step.

The subsequent WSGM `OtherManagers` integration adds exact-Atlas Full-mode takeover of those ten
services, three tasks, two MSIX families and the dedicated controller keyboard (since 2026-11-07 the
desktop family is left out; see the section above). Those identities are
recorded in `AcerModels.ManagerApps`, bound to the PA08-I51 definition, and reach the host through
`HandheldDevice.ManagerApps`. It records changes before dispatch, propagates restart requirements to
setup, rechecks accepted policy at startup and every five minutes, and restores recorded changes on
uninstall. This is host application policy; LibHandheld continues to own rear-button mode and
physical device protocols. The implementation is awaiting installation and live runtime acceptance;
the historical local guard is not that proof. Runtime F22/long-press routing remains unsupported
while the keyboard source is disabled. Recovery scripts and private captures remain local artifacts.
The operational reference above distinguishes the two ownership/recovery paths.

Recovery uses captured model/firmware identity; lighting also binds the controller release. Unknown
observations remain unknown, writable controls do not require current-value readback, and uncertain
commands are not retried. Profile edits require retained bytes so unrelated fields cannot be
invented. Fan recovery uses exact original behavior/speed where known; otherwise it returns to
firmware Auto and reports incomplete verification.

The optional KX helper could not load its kernel driver: Windows security returned error 225.
Protection was left enabled. This affects optional processor tuning, independently of the native EC
power path. Missing or blocked helpers may therefore leave the aggregate device state degraded while
native Atlas services remain usable.

Native source identities match the earlier supplied HardwareService and Agent hashes. Battery
protocol inspection used installed AcerCCAgent.exe 1.5.51, SHA-256
`41e0016cfb45b7d3dcbd7568ca118cc498365b452e631e07e0d225293330c530`, with Ghidra 12.2 and ILSpy 11.2.
Protocol implementations are independent code; proprietary binaries and decompilation remain
private. Windows sensor reference axes follow
[Microsoft's sensor orientation contract](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/sensor-orientation).

The subsequent six-pose/three-rotation attended trial confirmed both Windows motion axis mappings as
`(X, Z, -Y)`. See [motion evidence](MOTION_TESTS.md) for the measured signs, hand-positioned
rotation integrals and unresolved accelerometer magnitude asymmetry; axis acceptance is not scale
calibration.

HardwareVerified remains false. Runtime front-long handling, View/Menu attribution, full left-stick
travel, precise motion scale calibration, held-button disconnect, actual Windows sleep/wake and
visible Steam acceptance were not established by autonomous software trials. Library suspend/resume
is not an OS sleep test. Builds, fixtures and Overlay replays do not replace those observations.

## Follow-up: presets and signed processor access (2026-10-27)

The sustained-limit descriptor now publishes Quiet, Balanced and Turbo as firmware-managed presets.
Their zero watt fields are unspecified placeholders, never commands. Hosts must select the declared
AC/DC scenario and Windows mode without subsequently writing PL1/PL2. Full Power selects Manual with
the firmware-advertised sustained/boost maxima (40/65 W on this unit) and Windows Best performance.
The host retains Custom and per-game/AC/DC assignment policy.

Windows blocked KX's HwRwDrv.x64; a later read attempt returned certificate-revocation
error 2148204812. Atlas no longer requires IntelKx: optional voltage settings use the existing
pinned, signed PawnIO IntelMSR module's allowlisted MSR 0x150 mailbox. Neutral 0 mV commands were
accepted on Core, Cache, GPU and System Agent on this device. This proves command acceptance, not a
measured voltage change or acceptance of nonzero offsets. GPU-clock MMIO writes remain unavailable
because the signed MCHBAR module has no write export. Other device families retain their existing
backend. Mailbox commands dispatch once, check busy/completion status, and never retry uncertain
writes. Module source: https://github.com/namazso/PawnIO.Modules/blob/main/IntelMSR.p

Rear paddle descriptors carry no extra Guide/QAM forwarding marker. Physical RearPaddle1/2 states
still arrive through canonical controller input. This resolves the WSGM host rejecting the entire
OEM descriptor set when a rear enum occupied the Guide/QAM-only forwarding field. Front long-press
keyboard events remain suppressed by the separately disabled keyboard leaf described above.

## Stock-profile limit observations (2026-10-27)

A read-only follow-up queried command 0002/get (payload [00 A0 02 00 02 selector]) for selectors 0,
1, 2 and 3. Firmware echoed each selector at offset 5. Observed PL1/PL2 values at offsets 6/10 were
35/65, 40/65, 30/65 and 40/65 W respectively. These are observations on this unit, not a hard-coded
stock wattage table. Mode 3 was active during the inventory. The old service queried only Manual and
withheld numeric power in every stock mode, which explains the first-launch unknown TDP. ReadCurrent
now keeps Manual bytes for edits while publishing limits read from the active stock profile. Missing
getters still do not gate writes.

## Live fan scheduling correction (2026-10-27)

The 1779 runtime exposed a brief stale-temperature warning every ten seconds despite valid CPU
samples and working fan writes. The common serializer's full maintenance branch skipped its
read-only telemetry hook, creating a four-second sensor gap against the host's three-second fan
window. Every observation turn now invokes the telemetry hook after any maintenance and before
publication. Maintenance remains on its ten-second schedule; temperature freshness limits and
firmware-auto fallback are unchanged. A focused regression forces a maintenance turn and asserts
maintenance/telemetry/publication order. Runtime acceptance must span multiple maintenance turns.

Superseded the same week: the fix ran telemetry twice on families whose full refresh already samples
(Claw, Ally, OneXPlayer, profiled fans). The serializer again runs either the full refresh or the
telemetry hook. A full refresh must sample what the hook samples, so the Atlas fan service's refresh
reads RPM and CPU temperature, and the Steam Deck's also measures clocks. The same Atlas runtime
acceptance across several maintenance turns applies.

## Power readout after a mode selection (2026-10-27)

The initial stock-profile observation fix did not cover a later scenario command: selecting a stock
mode still cleared both power observations, and control values are not periodically polled. Mode
commands now best-effort read the selected profile, then publish that pair immediately after
firmware accepts the mode. Manual selection publishes its own saved pair rather than retaining the
previous stock limits. Getter failure never blocks a mode write and leaves its numeric limits
unknown. Two focused regressions cover mode changes and unavailable getters.

## Rejected auxiliary HID cloaking (2026-10-27)

An isolated Steam SDL inventory identified four extra Xbox entries as F901 MI_01/COL01 and
MI_02/COL03, COL04, COL05. Hiding those auxiliary leaves reduced enumeration from five pads to one
Steam Deck, but the maintainer then lost both physical and RustDesk keyboard input. Turning cloaking
off, removing the entries and stopping WSGM did not restore input; uninstalling HidHide did. That is
a failed hardware trial, regardless of its initial controller-list result.

The auxiliary-hide profile changes were withdrawn. Do not add these vendor/consumer/OEM HID
collections to the kernel hide set. Keep HidHide scoped to the actual XInput controller nodes. Any
suppression of SDL's auxiliary Xbox identities must happen inside the consuming application, without
blocking Windows keyboard, mouse, consumer or OEM collections.

## DInput rumble discovery (2026-10-27)

A focused F90A capability query found gamepad input report 01 (64 bytes), output report 05 (32
bytes, vendor pageFF00), and 17 button usages. A rumble-only report 05 with byte1=01, right/high
amplitude at4 and left/low at5 produced a pulse the maintainer explicitly felt. Accelerometer RMS
increased from 0.00324 g idle to 0.04231 g during the 600 ms pulse. A zero-amplitude report stopped
it, and the original XInput/rear-mode tuple0100000100 was restored and verified. This establishes a
DInput rumble path; it does not establish motor-side mapping or the complete DInput OEM-button map.
A subsequent button capture recorded no transitions because the maintainer had left the device and
was remote-only. Do not infer missing buttons from that empty capture. Physical button acceptance
remains pending while source/descriptor investigation continues.

## DInput runtime integration (2026-10-27)

Runtime controller ownership now selects DInput 2 / Combination 1 and returns the exact original
mode pair on release. The command lane follows the same physical port from F901/FFA0:1 to
F90A/FFF0:40 after one mode dispatch; it waits for enumeration without retrying the write. Input
uses the generic descriptor parser for the F90A gamepad. The actual gamepad leaf and old XInput
gamepad nodes form the hide set; auxiliary HID collections are not hidden. Report05 rumble uses an
independent output handle and sends zero before releasing it. Lighting continues through the
correlated command lane. Windows IMU remains the motion source.

An on-device library lifecycle run reached Active with every service Owned, produced 128 controller
samples in one second, accepted rumble and zero output, disabled/re-enabled controller management,
suspended/resumed, and stopped Clean. All mode changes restored correctly. The generic parser uses
the 17-button descriptor and the OEM PS/camera/FL1/FR1 naming for face/OEM mapping. Those physical
button mappings still require attended acceptance; the empty remote-only capture is not evidence
that buttons are absent. No Steam executable, SDL setting or Windows keyboard filter is changed by
this route.

## Confirmed DInput companion hide set (2026-10-27)

After switching to F90A, two additional non-gamepad identities remained. Following the maintainer's
explicit request to hide those two, each was tested separately: the command collection FFF0:0040 at
MI_00/COL02 and the consumer collection000C:0001 at MI_01/COL03. The fresh SDL inventory became
exactly one Steam Deck. The maintainer confirmed keyboard input still worked, and confirmed the
Steam list after Steam restarted. This supersedes the earlier blanket prohibition for these two
measured DInput collections only; the failed F901 auxiliary set remains forbidden.

The library selects these exact release0100 report shapes on the selected gamepad's physical port.
The keyboard MI_01/COL01 and mouse MI_01/COL02 do not match. WSGM's existing ownership ledger adds
and restores the two entries. No Steam filtering or global backend override is involved.

## Atlas stock power ordering (2026-10-28)

The desktop application's enum calls 0 Turbo and 1 Balance, but the PA08-I51 V1.03 EC stock table
contains 35/65 W at index 0, 40/65 W at index 1, and 30/65 W at index 2. The maintainer observed the
resulting inverted Turbo/Balanced readouts. Atlas now names the measured power tiers Balanced 0,
Turbo 1 and Quiet 2. All three retain 65 W boost because that is what their firmware profiles
contain; no invented boost value or manual-limit write is added to a stock selection. Native service
tracing confirmed that the OEM path selects a complete profile and only writes numeric fields for
Manual 3. Originals continue to be stored as raw mode numbers, preserving exact restoration.

## Keyboard-interface hiding withdrawn (2026-10-28)

The deployed F90A companion set stopped all keyboard input again, physical and remote. Both failures
hid a collection on the keyboard's own USB interface: F901 MI_02/COL03..05 beside keyboard
MI_02/COL01, and the F90A consumer collection MI_01/COL03 beside keyboard MI_01/COL01. The consumer
collection is no longer a companion. The shared hide set now drops any identity whose USB interface
also carries a present keyboard or mouse collection, so no profile can repeat this. The MI_00/COL02
vendor collection shares an interface only with the gamepad and stays hidden; that remains attended
acceptance after HidHide is reinstalled.

Lighting edits are no longer restored on release. Restoring the journaled original brought the
firmware rainbow back every time WSGM stopped. Old lighting originals are retired unrestored.

## Explicit preset limits (2026-10-28)

The stock EC profiles keep PL2 at 65 W in all three tiers, and the platform's RAPL registers did not
follow them, so a stock selection changed only the EC's PL1 field and the TDP readout did not match
what the maintainer measured. At the maintainer's direction the Quiet, Balanced and Turbo presets
now write explicit PL1/PL2 through Manual 3: 20/20, 25/37 and 30/37 W, HandheldCompanion's Arc G3
(ClawCG3EM) table after its 20 W floor. Full Power keeps the firmware maximum, 40/65 W. Manual
limits were measured to bound package power (25 W requested, 24.6 W measured). The stock EC profiles
remain selectable through the scenario control.

## Follow-up: KX removed (2026-10-28)

KX and its HwRwDrv driver were removed from LibHandheld and WSGM. Every Intel model, not only the
Atlas, now uses PawnIO IntelMSR for processor settings, and the generic Intel package power path
writes MSR 0x610 through the same module. The Atlas keeps its native EC power path and does not use
that generic transport. Intel GPU clock control is no longer offered on any model.

## DInput input capture (2026-10-28)

A read-only attended capture of every 0502 collection, with Steam closed, measured the F90A gamepad
report 01. Triggers are analog Rx (left) and Ry (right), 0..255. Buttons 7/8 switch on at value 2,
so they no longer force a trigger to full. Rear-right is button 16 (M1) and rear-left button 17
(M2). L3/R3 are 11/12. The front OEM keys send nothing in any gamepad, vendor or consumer report: a
tap produces no event at all, a hold of front-left sends one Win+Tab pulse and a hold of front-right
sends F22 (VK 0x85, scan 0x6D) with auto-repeat and a release, both on the keyboard interface.
Predator Sense never selects DInput; its tap handling uses the XInput-topology vendor reports.
Firmware mode 3 ("Special") keeps the F901 XInput topology, including the IG_00 collection and the
auxiliary SDL identities: front-left tap 9E, front-right tap `37 81FF` on MI_02/COL04, rear 9A. The
probe restored the original mode 1/0 with verified readback.

Motion upload in Special mode: after mode 3/1 was selected, command 2F 01 was sent once and the
device was held still, then pitched, yawed and rolled. No collection reported anything during those
phases, including the FFA0 command lane. Predator Sense defines but never calls this command.
Command 2F 00 and the original mode 1/0 were restored and verified. Windows sensors remain the
motion source.

## Back to XInput with verified auxiliary hiding (2026-10-28)

DInput was abandoned because front-key taps never arrive in it. The managed mode is now XInput 1
with rear Combination 1. With WSGM and Steam stopped and the maintainer's drivers freshly updated,
an attended trial hid F901 MI_01/COL01, MI_02/COL05, MI_02/COL04 and MI_02/COL03 one at a time.
After each step the maintainer typed a confirmation through the keyboard path, and again with all
four hidden. Steam then enumerated exactly one controller, the XInput pad. The profile lists these
four exact leaves as AuxiliaryHidCollections, the only identities allowed past the
keyboard-interface guard. WSGM remains allowed to read them for the command lane and QAM report. The
earlier keyboard losses are explained in the correction below.

## Correction: the keyboard losses were not caused by hiding (2026-10-28)

The maintainer traced both "no keyboard" incidents to outside causes. GTA V Enhanced running on the
RustDesk client PC blocked RustDesk keyboard input, and the physical keyboard's USB-C hub had
failed. HidHide and the hidden HID leaves were not involved. The "Rejected auxiliary HID cloaking"
and "Keyboard-interface hiding withdrawn" conclusions above rest on that misattribution; the
attended trial in "Back to XInput with verified auxiliary hiding" is the valid evidence. The
keyboard-interface guard added on that premise has been removed; the profile's four
AuxiliaryHidCollections are now hidden with the pad like any other declared identity.

## Hardware verified (2026-10-28)

The maintainer accepted the integration on the PA08-I51 used for every session above, and the
definition is now marked hardware verified. Accepted in use on the device: the explicit power
presets and manual PL1/PL2 with AutoTDP, both fans in firmware, software-curve and manual control,
battery charge protection, shared and per-segment ring lighting, the managed XInput controller with
front buttons, triggers, Task/Predator as Guide/Quick Access and the M1/M2 rear buttons on their
printed sides, Windows motion, calibrated rumble, auxiliary-interface hiding without keyboard loss,
the Predator Sense takeover and the Game Mode boot. The DInput path remains a fallback used only
when the mode lane is unavailable; its button mapping has no separate attended acceptance.

## Attended fan speed calibration (2026-10-29)

Unit PA08-I51 on AC, EC power mode Manual (3), Acer services stopped, idle CPU 52..66 degrees C.
Speeds were commanded through `AcerGamingFunction` and read at health indices 4 (left) and 8 (right)
once per second; values below are settled medians. Every run ended with both fans in firmware Auto
and the original power mode selected.

The meaning of `SetGamingFanSpeed` depends on the left (`sys`) fan's behavior:

| Left fan | Right fan | Settled response to command c                                     |
| -------- | --------- | ----------------------------------------------------------------- |
| Auto     | Manual    | Right is absolute, about 70 RPM per step: 25 -> 1770, 100 -> 7000 |
| Manual   | either    | Each manual fan runs at about Auto + c x (7000 - Auto)            |

With the left fan manual, both fans first fall toward the absolute target for about five seconds and
then rise to the relative value. Behavior and speed getters keep reporting Manual and the commanded
speed throughout, so firmware does not switch back to Auto. Command 0 stops a fan until firmware
restarts it, repeatedly. The fans are independent and equal: L10/R100 settled at 3970/7012 and
L100/R10 at 6955/3902. One combined behavior write behaves like two single writes. The CPU and GPU
behavior lanes are rejected with status 0x1.

The Auto floor is the firmware fan table that `_Q30` selects from power mode and AC state through EC
`FANT`. Measured Auto: Quiet 2860, Balance 3550, Turbo 3645, Manual about 3600 RPM. This also
explains the 2026-10-27 trial, where Auto was about 2800 and a left-only 50% gave about 4100.

ACPI EC RAM is reachable through PawnIO `LpcACPIEC` on 0x62/0x66; offset 0x58 matched CTMP. Most
reads time out while Windows' EC driver shares that channel, and the signed module refuses the
second channel at 0x68/0x6C. ACPI exposes no fan output field, only RPM (`CPUF`, `GPUF`) and `FANT`.
No EC write was attempted.

Rewriting `SetGamingFanBehavior` 3 every second keeps a manual fan at the absolute command
indefinitely: both fans held about 1750 RPM at command 25 for 25 s, including while the CPU read 95
degrees C, and a 25 -> 60 -> 25 step settled within about 8 s. A 3 s cadence oscillated between 1700
and 2650 RPM, and rewriting only the speed did not hold. Each manual fan needs its own re-arm;
holding only the left fan let the right fan drift back to about 3900 RPM. Held speeds were linear at
about 70 RPM per percent (10: 780, 15: 1040, 25: 1750, 35: 2500, 100: 7000). Commands 5 and 0
stalled and restarted the fan repeatedly, so 10 is the lowest usable duty. Twenty consecutive
behavior writes took 48 ms each including the 30 ms vendor pacing and produced no measurable
all-core stall (2.5 ms maximum gap against a 4.1 ms idle baseline). The runtime now re-arms every
held fan at 1 Hz. The firmware's own thermal protection did not intervene within the tested window,
so the software curve is the only guard while a fan is held.

## Fan curve presets (2026-10-29)

The host's Quiet, Default and Aggressive software-curve presets for the Atlas are the definition's
`FanCurvePresets`, set in `AcerModels`. They were derived from the calibration above rather than
measured as shapes of their own: duty is an absolute share of full speed at about 70 RPM per percent
and 10 is the lowest duty that keeps a fan spinning, while firmware Auto holds about 3600 RPM (51%)
at the 52..66 degrees C idle. The shapes therefore rest at 10, 15 and 20 through 40 degrees C, stay
near inaudible until 60 degrees C and reach 100 at the processor's 100 degrees C limit:

| Preset     | 0..100 degrees C in 10 degree steps          |
| ---------- | -------------------------------------------- |
| Quiet      | 10, 10, 10, 10, 10, 10, 15, 25, 45, 75, 100  |
| Default    | 15, 15, 15, 15, 15, 20, 30, 45, 65, 85, 100  |
| Aggressive | 20, 20, 20, 20, 25, 35, 45, 60, 80, 100, 100 |

The EC package temperature swings ten or twenty degrees within a second or two at idle, and a held
fan accelerates by about six points a second, so the curve reads a ten-second average, recomputes
only after a 3 degree change and moves at most 5 points up or 2 down per second. The host's general
smoothing is a three-second average, 2 degrees and 10 up or 3 down. The shapes and smoothing are in
use on the hardware-verified definition; no separate thermal-load acceptance of each preset is
recorded.

## Lower power tiers (2026-10-29)

The Quiet preset inherited the MSI Claw 8 EX's 20 W floor, which the Atlas does not have. Selector 1
still advertises a 10 W PL1 minimum, but Manual already stored unified 8 and 9 W in the 2026-10-27
range test, Intel's Arc G3 configurable TDP starts at 8 W, and the maintainer confirmed on the
PA08-I51 that 8 W works. Both limits now accept the lower of selector 1 and 8 W; the maxima still
come from selectors 2 and 4. Three tiers sit below Quiet, all through Manual 3 with Windows Better
battery: Battery Saver 8/8, Efficiency 12/15 and Light 15/20 W. Quiet 20/20, Balanced 25/37, Turbo
30/37 and Full Power 40/65 W keep their ids and values. No instrumented package-power sweep below 25
W has been recorded.

## Selectable power-limit paths and Intel IPF (2026-10-31)

Attended on the PA08-I51, BIOS V1.03, on AC. The maintainer reported in-game lag with the EC power
path, and the same lag with Predator Sense. MSR 0x610 is unlocked (bit 63 clear). Writing PL1/PL2
there through PawnIO, with Manual opened once to the firmware maxima, felt better in game.

A read-only capture sampled package energy (MSR 0x611), MSR 0x610, MCHBAR 0x59A0 (signed IntelMCHBAR
read) and package thermal status (0x1B1) once a second. With PL1 50 W in the register, the package
ran about 50 W for about a minute, then MCHBAR PL1 stepped 40 -> 38 -> 36 -> 35 W and sustained
power followed it to 33..37 W. Re-selecting Manual every 5 s returned MCHBAR to 40 W and held about
41 W sustained, but FPS swung between 80 and 100, so the re-arm was removed. Manual's offset 8 field
(PL1 + 5 W in every stock profile, 30 W in Manual) changed nothing.

The MCHBAR writer was Intel's Innovation Platform Framework (`ipfsvc`, processor participant
`ipf_cpu`, sensors SEN2..SEN4). It overwrote a KX-written 45 W within 5 s at idle and ramped it back
to 40 W. With `ipfsvc` stopped, MCHBAR held 45 W for two minutes at idle. Under load the package
reached 100 C within about 15 s and something below the service (the BIOS or `ipf_cpu`) still
lowered MCHBAR PL1 to 35 W once the thermal thresholds tripped; it returned to 40 W within about 20
s of cooling. Windows 11 re-enabled Memory Integrity after setup cleared its scenario value
(`WasEnabledBy` 2), and HVCI blocked HwRwDrv (Code Integrity event 3077) until it was off.

The runtime therefore offers Firmware, PawnIO and KX, chosen by the user and stored globally. The
direct paths keep PL1 at the firmware's 40 W maximum, disable and stop IPF while selected, and
restore it, MSR 0x610 and MCHBAR on Firmware or release. KX loads HwRwDrv for one command and
unloads it on exit, so it runs only while KX is selected. Raw captures: local
`artifacts/acer-power-monitor-20261031-*.jsonl`. The path switch and IPF guard in this build have
not had an attended game session yet.

## KX removed again; Manual held on the firmware path (2026-11-01)

Maintainer decision, not an attended run. KX and HwRwDrv are removed from LibHandheld and WSGM
again, and setup no longer forces Memory Integrity or the driver blocklist off for the Atlas. The
power-limit choice is Firmware or PawnIO. PawnIO's signed IntelMCHBAR module only reads, so neither
path writes the MCHBAR mirror, and the firmware's Manual re-selection is what returns MCHBAR PL1 to
its maximum (2026-10-31 capture above).

The runtime now selects Manual on every limit write on both paths, because the firmware needs the
mode switch. On the Firmware path, while WSGM holds Manual, it rewrites the retained Manual profile
with the accepted PL1/PL2 and selects Manual every 3 s, Handheld Companion's TDP watchdog interval.
The rewrite and a command never interleave. A failed rewrite is not repeated at once: a Manual
readback keeps the hold for the next interval, anything else drops it and forgets the published
values. A stock mode, a path switch, release or suspend ends the hold. IPF and esifsvc stay disabled
and stopped on both paths while WSGM owns the limits, and are restored on release.

The maintainer attributes the 80..100 FPS swing seen with the earlier 5 s re-selection to the test
game running from the SD card, not to the re-selection. That is untested. This build has not had an
attended game session; held power, frame pacing and the IPF guard on the Firmware path remain to be
accepted on the PA08-I51.

## Attended power button capture (2026-11-01)

PA08-I51, BIOS V1.03, Windows 11 Pro 10.0.26300, with the Windows power button action set to Do
nothing for the capture and restored to Sleep (1/1) afterwards. A read-only probe listened on Raw
Input (System Control, consumer, keyboard), a low-level keyboard hook, direct HID reads, power
setting notifications and Kernel-Power/Kernel-ACPI ETW while the maintainer pressed the button.

The button reaches Windows through the Intel HID Event Filter (`ACPI\INTC10CC`, service
`HidEventFilter`). Its child `HID\INTC816` is a Portable Device Control collection (usage page 1,
usage 0x0D) with one button, System Power Down (0x81), in input report 0x1C. It reports both edges
as they happen: `1C01` pressed, `1C00` released. Measured holds were 9 ms, 1.90 s and 1.63 s. Two of
three quick taps a second apart arrived as `1C00` alone, with no press report. A normal user opens
the collection for shared reading and writing; Windows keeps working alongside it.

Windows' own path is Microsoft's Button Converter (`BUTTONCONVERTER\CONVERTEDDEVICE`), whose System
Control collection (`Col03`, report 3, usage 0x81) sends `0301` and `0300` together only at release,
so it carries no hold duration. Windows ignored the two release-only taps entirely and ran its
configured action 0.6 to 0.9 s after each other release (ETW `ExecutePowerAction`). Holding past
about 2 s made the converter report the press while held, and winlogon then started
`SlideToShutDown.exe`. Raw Input, the keyboard hook and the power notifications saw nothing.

Follow-up tests the same day with a stand-alone tool (Atlas8Tweaks power-button 1.0.0), all accepted
by the maintainer on the device: reading `INTC816` gives working short and long presses with
Windows' action set to Do nothing; an Image File Execution Options redirect for
`SlideToShutDown.exe` stops the slide screen; `SetSuspendState(false)` and
`NtInitiatePowerAction(PowerActionSleep)` both hibernate this S0-only machine (Kernel-Power 187,
then a firmware boot); `SC_MONITORPOWER` 2 enters Modern Standby (Kernel-Power 506, reason
SC_MONITORPOWER, woken by 507 Power Button); an injected Sleep key does nothing. Suspending while
the button is still held made Windows offer slide-to-shut-down after wake, so actions run on
release.

LibHandheld publishes the button from `IntelPdcPowerButtonService` as a `Power` placement control.
The Claw 8 investigation (WSGM #116) found an ACPI fixed-feature button with a press edge only, so
this source does not apply there. The WSGM integration built on it has not had an attended session.

## IPF power-limit path (2026-11-01)

Attended on the PA08-I51, BIOS V1.03, on AC, IPF 2.3.20303.5058. WSGM now asks Intel's Innovation
Platform Framework for PL1/PL2 over its elevated client session instead of fighting it. The protocol
is IPC mode of the DriverStore `ipfipc.dll`, reversed with Rizin and checked against Intel's
archived Apache-2.0 sources (github.com/intel/dptf: `ipf_ipc_iface.h`, `esif_sdk_iface_app.h`,
`ipf_apploader.c`): `GetIpcInterface` (type 8, version 1, 132 bytes, client SDK `1.0.11402`) ->
`Ipc_Init` -> `Ipc_SetAppIface` (AppInterfaceSet type 0, version 4, 192 bytes) ->
`IpcSession_Create` (`pipe://ipfsrv.elevated`) -> `IpcSession_Connect`, with
`IpcSession_WaitForStop` as the message pump on its own thread. `esif_data` is packed to 20 bytes.
The server then called `AppCreate` (with the primitive function), `ParticipantCreate` for TCPU and
its domains PKG/CPU/GFX, then the sensors. `GET_RAPL_POWER_LIMIT` (38) read 45/65 W;
`SET_RAPL_POWER_LIMIT` (130) with instance 0/1 = PL1/PL2 moved MCHBAR 0x59A0 to the request (30/50 W
in the probe) while MSR 0x610 stayed 45/65 W. `SET_RAPL_POWER_LIMIT_CLEAR` is refused for this role
(`ESIF_E_SESSION_PERMISSION_DENIED`, 3703); closing the session releases the request, after which
IPF chose 45/86 W itself.

Dead ends on the way, kept so they are not repeated: `ipf_uf.exe shell` and `client` print nothing
("IPF Shell Disabled"; DCFG `ShellAccessControl` or a release build without shell output); core mode
through `ipfcore.dll` needs an undocumented `SessionExecute` command syntax; `IpfCore_Init` returned
`ESIF_E_NO_MEMORY` only because the IPF processor participant (PCI `DEV_B01D`) had been disabled by
an earlier `Disable-PnpDevice` test, which hides the `{EE27098E-1B22-472A-89D8-5CCCE16B1356}`
interface it uses to find `InstallDir`. That device must stay enabled.

Result under OCCT with PL1 45 W, PL2 45 W on the IPF path: MCHBAR held 45/45 W for four minutes,
package power averaged 44.1-45.1 W with 49-51 W peaks, at 99-100 C. The firmware path had lowered
MCHBAR PL1 to 35 W within about 45 s at the same temperature, and 40 W on the IPF path also held
past three minutes. IPF arbitrates RAPL limits across its clients and the lowest request wins, so
this path holds a value only up to what Intel's own policy asks for; at 45 W it did not ask for
less. The PL1 maximum is the firmware's 40 W on every path: 45 W kept the package at its 99-100 C
limit, and the maintainer set the ceiling back to 40 W and dropped the Extreme Power preset
(2026-11-01). Raw captures: local `artifacts/acer-power-monitor-20261101-*.jsonl`; probes in
`artifacts/ipf-re`.

## IPF voltage discovery and GPU cap-register access (2026-11-07)

Attended research on PA08-I51, BIOS V1.04, installed IPF 2.3.20303.5058. The
[interface map](../../../../docs/intel-ipf-map.md) records REA/Ghidra binary identities, all 832
primitive names and current role permissions, and all 3,941 shipped platform bindings. This is
bounded protocol evidence, not acceptance of a new application control.

The ordinary elevated IPC session read PKG undervolt protection = 1, overclocking enabled = 0 and
overclocking secured = 1. XTU and DPTF telemetry endpoints refused connection with 3108. No voltage
or mailbox write, security-policy change or service restart was attempted.

GFX primitive 987 read the original cap-register byte as 255. Primitive 304 accepted raw 120, which
read back as 120 immediately and after two seconds. One restoration to raw 255 succeeded and read
back as 255; a separate session after disconnect also read 255. No write was retried. The probe used
a distinct application name and WSGM stayed running. GPU frequency-table read 137 was denied with
3703; the measured RP capability bytes from primitive 277 were 132/0/6. Actual frequency under load,
MHz conversion, arbitration and sleep/reset behavior remain unverified. No GPU or undervolt
capability was added and this run does not change the model's overall hardware acceptance status.

Local logs and probe source: `artifacts/ipf-map-20261107/`, especially `live-cap-write.txt` and
`live-after-disconnect.txt`.

## Pad stall recovery removed (2026-11-01)

Maintainer decision, not an attended run. The maintainer reports that a newer Atlas BIOS fixes the
pad hanging in XInput after Modern Standby, so the stall recovery added on 2026-10-30 was removed:
the reader watchdog, the USB port cycle and the profile flag that enabled them. The BIOS version was
not recorded. A pad that stops answering is now handled only by the ordinary disconnect and
reacquire path.

## Extended VRR range (2026-11-01)

Source: the maintainer's own
[Atlas8Tweaks VRR Fix 1.0.0](https://github.com/KillerPixelCrew/Atlas8Tweaks/tree/main/vrr-fix)
(`engine/AcerAtlasVrr.ps1`), clone in `_ref/Atlas8Tweaks` at `89bfb6c`. Its approach follows the
ClawLab MSI Claw Intel VRR Range Fix 3.0.0 (MIT); the Intel driver calls it borrowed are attributed
in LibGPUDriverInteract, where they live. The floor, flicker and LFC facts below come from the
Atlas8Tweaks testing on a PA08-I51, not from a new attended WSGM pass.

- Panel: BOE `0CFF` NS080WUM-LX1, 1920x1200, a 256-byte EDID (base block plus one DisplayID
  extension). The validated physical EDID has SHA-256
  `F05B01B33B82B9597AE7BB15F5EF43CAEDB9194BF4A81B3008D34E8DBAC6ECCE`. Its VRR range is 48-120 Hz.
- Floor fields, all 48 on the physical EDID: offset 95 (base range descriptor), 142 (DisplayID
  dynamic timing range limits), 154 (AMD FreeSync VSDB), 174 and 180 (DisplayID Adaptive-Sync
  descriptors 1 and 2). A variant sets every one to the new floor, recomputes the DisplayID section
  checksum over 129..253 into 254, then both block checksums. Setting them back to 48 must give the
  exact physical EDID, which is how an override Windows reports is traced back to this panel.
- The earlier test build patched every floor field except offset 180. Its variants, and every floor
  from 10 to 47 with either offset set, are recognized as this fix's own; any other override is
  another tool's and is never replaced.
- The panel visibly flickers below 28 Hz, so 28 Hz is the floor. Intel's low frame rate solution
  (LFC) engages below 48 fps whatever the EDID floor and switches between x2/x3/x4 multipliers,
  which judders; with it off, 30-120 fps run native VRR, frames below the 28 Hz floor show exactly
  twice and 25-28 fps cadence is mixed. The high frame rate solution stays on.
- With the 28 Hz EDID active, Intel Arc Sync EXCELLENT reports 28-120 Hz. Factory state measured
  before any change: Arc Sync CUSTOM 48-120 Hz, frame time limits 0/0 us, LFC on, high frame rate
  solution on.

`ExtendedVrrPanel` carries these facts on the Atlas definition. `AcerExtendedVrrTests` checks the
variant arithmetic against hashes the script's own functions produced on a synthetic EDID; the real
physical EDID is not in the repository, so the real hash match is the attended pass's to confirm.
