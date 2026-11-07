# Atlas 8 OEM input and software control

Measured on Acer Predator PA08-I51, PTL F900_PTG, BIOS V1.03, controller 0502:F901 release 0100, on
2026-10-27. These are exact model findings, not a policy for every Acer PC.
[Provenance](PROVENANCE.md) records the source research and test limits.

Since 2026-11-07 Predator Sense for Handheld must be free to start: Acer delivers Atlas BIOS updates
only through its Live Update. The software disable below is the measured 2026-10-27 configuration and
remains evidence of what the Acer stack does; it is no longer applied by WSGM or by a maintenance
guard. Only the dedicated keyboard leaf disable stays managed, because it is firmware input that
exists with or without the app. See [Predator Sense allowed to start again](PROVENANCE.md#predator-sense-allowed-to-start-again-2026-11-07).

## Where input comes from

| Physical action                           | Device and report                                                                           | Meaning and prerequisites                                                                                                                                                                                     |
| ----------------------------------------- | ------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Front-left quick tap                      | Controller FFF1:00F5 vendor input, interface 01, 15 bytes: `9E0000050000000000000000000000` | Guide/Task notification. No hardware release; LibHandheld emits a bounded 200 ms tap. Works with Acer software absent and the separate USB keyboard disabled.                                                 |
| Front-left hold                           | USB keyboard `HID\VID_0502&PID_F901&MI_02&Col01`, usage 0001:0006, service `kbdhid`         | Complete, non-injected LeftWin-down, Tab-down, LeftWin-up, Tab-up. No simultaneous Guide HID notification was captured. Persists without Acer software; disappears when this exact keyboard leaf is disabled. |
| Front-right quick tap                     | Controller 0088:0001 QAM collection, interface 02: `3781FF` then `370000`                   | QAM press/release pulse, about 100 ms. This is a separate collection from the keyboard. Works with Acer software absent and the keyboard disabled.                                                            |
| Front-right hold                          | Same USB keyboard leaf as Win+Tab, virtual key F22 / 85 hex, scan code 6D hex               | Down, autorepeat, up. Removed by disabling the keyboard leaf. Runtime long-press routing is not implemented; do not advertise a usable hold while its source is disabled.                                     |
| Rear-left                                 | Vendor report `9A04...`, release `9A00...`                                                  | Protocol M2, canonical RearPaddle1 (left, L4). Requires XInput/Combination M-key mode.                                                                                                                        |
| Rear-right                                | Vendor report `9A02...`, release `9A00...`                                                  | Protocol M1, canonical RearPaddle2 (right, R4). Requires the same mode.                                                                                                                                       |
| Both rear buttons                         | Vendor `9A06...`; partial release retains the other bit                                     | Independent held states; unrelated `9B...` reports do not release them.                                                                                                                                       |
| Power button                              | Intel HID Event Filter PDC `HID\INTC816`, usage 0001:000D, report `1C01` / `1C00`           | System Power Down 0x81, press and release as they happen. Quick taps sometimes send only `1C00`. Windows sees it later through the Button Converter. See provenance, 2026-11-01.                              |
| Volume Up / Down                          | I2C HID `HID\1025174B&Col08`, consumer reports `33E900` / `33EA00`, neutral `330000`        | Separate from the USB keyboard. Windows also projects volume-key events with an unnamed Raw Input source. Physical press/release reports survived the exact USB keyboard disable.                             |
| Ordinary pad buttons, sticks and triggers | Attributed 0502:F901 standard XInput controller                                             | Separate standard controller source; no OEM software required.                                                                                                                                                |

For release 0004 the published vendor input is 16 bytes. The parser admits that exact shape too, but
the measured controller-mode/lighting transports admit release 0100 only. Do not apply the 0100
transport merely because another controller has the same VID/PID.

## Software side effects versus firmware input

The Guide HID report was followed about 200 ms later by injected LeftWin+G while Acer software ran.
Stopping the Acer software removed Win+G and the Predator Sense launch, while Win+Tab remained. The
exact injecting process was not traced; the result identifies the Acer user-mode stack as the
dependency, not a particular executable within it.

Low-level keyboard observation showed all four Win+Tab edges. Raw Input attributed the Windows-key
edges and Tab release to the controller keyboard; that capture did not contain Tab down even though
the simultaneous global hook did. This is not evidence for an orphan-only global suppression rule.
Device-attributed input, not timing alone, identifies this source. No global Win+G, Win+Tab or
Alt+Tab blocker was added for Atlas.

Unlike the Claw's shared firmware PS/2 keyboard case, Atlas volume is on an independent I2C device.
Do not disable `i8042prt`, `kbdhid` globally, the composite USB controller/interface, or the I2C
device to suppress the Atlas shortcut.

## What makes rear presses arrive

The untouched unit was XInput mode 1, M-key Macro mode 0. Rear presses produced neither vendor
button reports nor XInput changes. No user-created macro or assignment is required: the library now
selects XInput 1 / Combination 1 while the host manages the controller.

The installed Acer DeviceProxyService defines these commands on the dedicated FFA0:0001, 64-byte
input/output command collection:

- Read: output prefix `0F00003C26`; response `1000003C27`, with gamepad mode at byte 5 and M-key
  mode at byte 6.
- Select: output prefix `0F00003C24`, followed by the two modes. Managed target: `0F00003C240101`,
  padded to the advertised 64 bytes.

`AcerControllerModeService` joins the shared lifecycle before OEM/controller readers, with
`RequiresControllerOwnership=true`. Controller management off does not acquire it. Acquisition
reconciles pending recovery, captures the original two modes, persists the original in the
firmware-bound journal, then sends one mode selection. It does not rewrite profile mappings, macros,
calibration, lighting, ROM or the other feature fields. An already managed controller needs no
write. There is no automatic retry of an uncertain selection.

Release, controller-management disable and suspend restore the original mode. The accepted write
completes the restore and retires the recovery entry; the mode read afterwards only goes to the log,
so neither a mismatch nor an unavailable observation keeps the entry or repeats the write. The CLI trial and attended rear capture verified 1/0 -> 1/1 -> 1/0,
independent left/right presses, simultaneous presses and clean releases. This mechanism is
implemented in LibHandheld; users need no Predator Sense preparation.

## Persistent software targets applied on this machine

The following is the tested installed stack's disable set. The minimal causal subset was not
isolated service by service. Optional Care Center, lighting, camera and telemetry entries are listed
because the maintainer requested all installed Acer software off, not because each one is required
to remove Win+G. Hardware drivers are retained.

| Target type                     | Exact names                                                                                                   | Applied state             |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------- | ------------------------- |
| Predator core/lighting services | `AASHHSvc`, `AcerHandheld.Service`, `AcerLightingServiceHH`, `ALSSvc`                                         | Stopped; startup Disabled |
| Other installed Acer services   | `AcerCCAgentSvis`, `AcerDeviceEnablingServiceV2`, `AcerORDService`, `AcerServiceSvc`, `AFZsvc`                | Stopped; startup Disabled |
| Acer telemetry service          | `UEIPSvc`                                                                                                     | Stopped; startup Disabled |
| Scheduled tasks in `\`          | `PredatorHandheldLauncher`, `UEIPInvitation`, `UbtFrameworkService`                                           | Disabled                  |
| Packaged desktop app            | `ULICTekInc.PredatorSenseforHandHeld`, family `ULICTekInc.PredatorSenseforHandHeld_nt9dgb7efx6bt`             | Package status Disabled   |
| Packaged widget                 | `ULICTekInc.PredatorSenseforHandHeldWidget`, family `ULICTekInc.PredatorSenseforHandHeldWidget_nt9dgb7efx6bt` | Package status Disabled   |

`AASHHSvc` hosts AcerCentralService, which starts AcerAgentService and the user-session
AcerHardwareService. Stopping services also removed the other associated service children. The
Desktop and Widget processes were stopped after recording and verifying their package paths.

Stopping processes and services alone was insufficient: Handheld.Widget returned after sign-in. Its
manifest registers `microsoft.gameBarUIExtension` / `Widget` and it was activated with an AppX
server name. The desktop package also registers the `predatorsense:` protocol and execution alias.
Neither package declares a startup-task extension in the inspected manifest.

An elevated
`Windows.Management.Deployment.PackageManager.SetPackageStatus(fullName, PackageStatus.Disabled)`
disables activation while retaining installed files. Both installed packages then reported
Disabled/NotAvailable. Explicit IApplicationActivationManager activation with the no-error-UI option
returned `0x80073D5A`, PID 0, for both. No Acer/Predator processes remained. Calling the same API
from the post-reboot non-elevated token failed Access Denied; elevation was required and verified.

Microsoft documents the
[Disabled package status](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packagestatus.disabled)
and the
[deployment status flag](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagestatus).

### Keeping the identified targets disabled (retired 2026-11-07)

From 2026-10-27 to 2026-11-07 the maintainer's machine ran the elevated local scheduled task
`\WSGM-Atlas-OemDisabledState` at sign-in and every five minutes. It reapplied Disabled to the ten
service names, three task names and two package identities above, through the admin-protected copy
`C:\ProgramData\WSGM\AtlasOemControl\Keep-Acer-OemDisabled.ps1` with health in `last-check.json`.

The guard is retired because Acer's BIOS delivery needs these targets. WSGM now tracks
`eng/retire-atlas-oem-guard.ps1` instead: run elevated once on that machine, it unregisters the task
and deletes the installed copy, and changes nothing else. The guard disabled the targets before WSGM's
takeover ran, so WSGM holds no records for them there; restore them from the local snapshots with
`artifacts/Restore-Acer-OemSoftware.ps1`.

## Exact keyboard disable and reboot result

Target only the unique, attributed keyboard leaf under the built-in controller:
`HID\VID_0502&PID_F901&MI_02&Col01`. On this unit its current full instance ID is
`HID\VID_0502&PID_F901&MI_02&COL01\7&338D4E88&0&0000`. Instance suffixes are machine-local; future
automation must resolve and verify the exact collection and built-in USB parent, not hard-code this
suffix or select every keyboard.

Normal Disable-PnpDevice returned Not Supported and normal PnPUtil disable refused a required system
device. The maintainer authorized the exact target; PnPUtil `/disable-device <id> /force` accepted
it and explicitly required reboot. Before reboot it still reported problem code 0. After reboot it
reported problem code 22 (disabled), and the maintainer confirmed that the left hold no longer
invoked Win+Tab. No forced reboot was hidden inside the disable operation.

The post-reboot capture verified three short Guide taps, three short QAM taps and three presses of
each volume button, including every release. There were no reader faults, lost events or injected
Win+G. The other HID collections and XInput interface enumerated healthy. This establishes the
tested subset, not every possible control or runtime UI path after disable. F22 belongs to the
disabled leaf and is intentionally unavailable in this configuration.

Before the requested reboot, FSE startup was set to HKCU
`Software\Microsoft\Windows\CurrentVersion\GamingConfiguration\StartupToGamingHome=0` (DWORD), with
machine Winlogon shell `explorer.exe` and no per-user shell override. The value remained 0 after
reboot. FSE is a separate Windows startup preference, not Acer software.

## Recovery and WSGM management

Record original service start/running states, task enabled states, package status bits, keyboard
problem/status and FSE value existence/type before mutation. Preserve the first snapshot. The local
original snapshots are copied into `C:\ProgramData\WSGM\AtlasOemControl`.

To restore OEM software, first stop and unregister `WSGM-Atlas-OemDisabledState` so it cannot
reapply the disable (`eng/retire-atlas-oem-guard.ps1` in WSGM does this). Clear only the package Disabled bit with elevated
`PackageManager.ClearPackageStatus(fullName, PackageStatus.Disabled)` when it was not originally
set; retain other status flags. Restore each service/task to its recorded state, not blanket
Automatic/Enabled. Then reopen Predator Sense normally if desired. The local
`artifacts/Restore-Acer-OemSoftware.ps1` consumes these snapshots.

The keyboard is a separate opt-in change: use `pnputil /enable-device <exact id>` and honor any
reboot requirement. The local `artifacts/Enable-Acer-OemKeyboard.cmd` contains the measured
instance. Restoring software does not implicitly re-enable this keyboard or FSE.

WSGM's `OtherManagers` integration extends its Full-mode/setup takeover and Settings > System
takeover action to this exact LibHandheld definition. From 2026-10-27 its catalog entry, published as
`AcerModels.ManagerApps` through `HandheldDevice.ManagerApps`, named the ten services and three
tasks above, the two exact MSIX package families and the dedicated
`HID\VID_0502&PID_F901&MI_02&COL01` keyboard identity. Since 2026-11-07 it names only the keyboard
identity, so Predator Sense, its `PredatorHandheldLauncher` task, its services and its packages start
as Acer installed them and its Live Update can deliver BIOS updates. WSGM discovers the current
keyboard instance instead of copying this machine's suffix, uses the targeted PnPUtil force-disable
operation, and propagates restart-required outcomes into setup. It never disables a composite parent
or a shared keyboard service.

Each change is recorded before mutation. Setup (install, update or repair, elevated) puts back once
every recorded service, task and package change the catalog no longer asks for, and only where the
setting still holds the value WSGM wrote; a state the user set afterwards stays. Uninstall restores
every remaining record. Accepted takeover is reconciled at session startup and every five minutes; a
non-elevated session reports changes it cannot apply rather than prompting during startup. The host
owns application policy and these install-lifecycle recovery records; LibHandheld continues to own
only physical input/controller mode and device protocols.

Disabling the keyboard still removes F22, so the implementation does not add native front-right
long-press routing. With Predator Sense running again, its injected Win+G and Predator Sense launch
after a Guide tap come back, and Acer's lighting, power and controller-mode writes share the routes
WSGM uses; neither has been measured together with WSGM yet.

## Remaining acceptance

HardwareVerified remains false. View/Menu attribution and full left-stick leftward travel need
isolated confirmation. Both Windows motion axis mappings and XInput perceptual rumble calibration
were subsequently confirmed; see [motion evidence](MOTION_TESTS.md) and
[rumble provenance](PROVENANCE.md#attended-rumble-calibration-2026-10-27). Precise motion scale,
held-button disconnect, actual Windows sleep/wake and live WSGM/Steam UI routing remain unverified.
Library suspend/resume and fixture/build success do not establish those behaviors. Optional Intel
voltage settings use the signed PawnIO IntelMSR module; Acer EC power does not depend on it. Nothing
in this workflow requires disabling Defender, driver signing, a shared PS/2 service or Windows
keyboard support globally.
