# Atlas 8 OEM input and software control

Measured on Acer Predator PA08-I51, PTL F900_PTG, BIOS V1.03, controller
0502:F901 release 0100, on 2026-10-27. These are exact model findings, not a policy
for every Acer PC. [Provenance](PROVENANCE.md) records the source research and test limits.

## Where input comes from

| Physical action | Device and report | Meaning and prerequisites |
| --- | --- | --- |
| Front-left quick tap | Controller FFF1:00F5 vendor input, interface 01, 15 bytes: `9E0000050000000000000000000000` | Guide/Task notification. No hardware release; LibHandheld emits a bounded 200 ms tap. Works with Acer software absent and the separate USB keyboard disabled. |
| Front-left hold | USB keyboard `HID\VID_0502&PID_F901&MI_02&Col01`, usage 0001:0006, service `kbdhid` | Complete, non-injected LeftWin-down, Tab-down, LeftWin-up, Tab-up. No simultaneous Guide HID notification was captured. Persists without Acer software; disappears when this exact keyboard leaf is disabled. |
| Front-right quick tap | Controller 0088:0001 QAM collection, interface 02: `3781FF` then `370000` | QAM press/release pulse, about 100 ms. This is a separate collection from the keyboard. Works with Acer software absent and the keyboard disabled. |
| Front-right hold | Same USB keyboard leaf as Win+Tab, virtual key F22 / 85 hex, scan code 6D hex | Down, autorepeat, up. Removed by disabling the keyboard leaf. Runtime long-press routing is not implemented; do not advertise a usable hold while its source is disabled. |
| Rear-left | Vendor report `9A04...`, release `9A00...` | Protocol M2, canonical RearPaddle2. Requires XInput/Combination M-key mode. |
| Rear-right | Vendor report `9A02...`, release `9A00...` | Protocol M1, canonical RearPaddle1. Requires the same mode. |
| Both rear buttons | Vendor `9A06...`; partial release retains the other bit | Independent held states; unrelated `9B...` reports do not release them. |
| Volume Up / Down | I2C HID `HID\1025174B&Col08`, consumer reports `33E900` / `33EA00`, neutral `330000` | Separate from the USB keyboard. Windows also projects volume-key events with an unnamed Raw Input source. Physical press/release reports survived the exact USB keyboard disable. |
| Ordinary pad buttons, sticks and triggers | Attributed 0502:F901 standard XInput controller | Separate standard controller source; no OEM software required. |

For release 0004 the published vendor input is 16 bytes. The parser admits that exact
shape too, but the measured controller-mode/lighting transports admit release 0100 only.
Do not apply the 0100 transport merely because another controller has the same VID/PID.

## Software side effects versus firmware input

The Guide HID report was followed about 200 ms later by injected LeftWin+G while Acer
software ran. Stopping the Acer software removed Win+G and the Predator Sense launch,
while Win+Tab remained. The exact injecting process was not traced; the result identifies
the Acer user-mode stack as the dependency, not a particular executable within it.

Low-level keyboard observation showed all four Win+Tab edges. Raw Input attributed the
Windows-key edges and Tab release to the controller keyboard; that capture did not contain
Tab down even though the simultaneous global hook did. This is not evidence for an orphan-only
global suppression rule. Device-attributed input, not timing alone, identifies this source.
No global Win+G, Win+Tab or Alt+Tab blocker was added for Atlas.

Unlike the Claw's shared firmware PS/2 keyboard case, Atlas volume is on an independent I2C
device. Do not disable `i8042prt`, `kbdhid` globally, the composite USB controller/interface,
or the I2C device to suppress the Atlas shortcut.

## What makes rear presses arrive

The untouched unit was XInput mode 1, M-key Macro mode 0. Rear presses produced neither
vendor button reports nor XInput changes. No user-created macro or assignment is required:
the library now selects XInput 1 / Combination 1 while the host manages the controller.

The installed Acer DeviceProxyService defines these commands on the dedicated FFA0:0001,
64-byte input/output command collection:

- Read: output prefix `0F00003C26`; response `1000003C27`, with gamepad mode at byte 5
  and M-key mode at byte 6.
- Select: output prefix `0F00003C24`, followed by the two modes. Managed target:
  `0F00003C240101`, padded to the advertised 64 bytes.

`AcerControllerModeService` joins the shared lifecycle before OEM/controller readers, with
`RequiresControllerOwnership=true`. Controller management off does not acquire it.
Acquisition reconciles pending recovery, captures the original two modes, persists the
original in the firmware-bound journal, then sends one mode selection. It does not rewrite
profile mappings, macros, calibration, lighting, ROM or the other feature fields. An already
managed controller needs no write. There is no automatic retry of an uncertain selection.

Release, controller-management disable and suspend restore the original mode and observe
it. Verified restore returns Idle; unavailable observation is unverified, and a mismatched
readback retains a failed recovery entry. The CLI trial and attended rear capture verified
1/0 -> 1/1 -> 1/0, independent left/right presses, simultaneous presses and clean releases.
This mechanism is implemented in LibHandheld; users need no Predator Sense preparation.

## Persistent software targets applied on this machine

The following is the tested installed stack's disable set. The minimal causal subset was
not isolated service by service. Optional Care Center, lighting, camera and telemetry entries
are listed because the maintainer requested all installed Acer software off, not because each
one is required to remove Win+G. Hardware drivers are retained.

| Target type | Exact names | Applied state |
| --- | --- | --- |
| Predator core/lighting services | `AASHHSvc`, `AcerHandheld.Service`, `AcerLightingServiceHH`, `ALSSvc` | Stopped; startup Disabled |
| Other installed Acer services | `AcerCCAgentSvis`, `AcerDeviceEnablingServiceV2`, `AcerORDService`, `AcerServiceSvc`, `AFZsvc` | Stopped; startup Disabled |
| Acer telemetry service | `UEIPSvc` | Stopped; startup Disabled |
| Scheduled tasks in `\` | `PredatorHandheldLauncher`, `UEIPInvitation`, `UbtFrameworkService` | Disabled |
| Packaged desktop app | `ULICTekInc.PredatorSenseforHandHeld`, family `ULICTekInc.PredatorSenseforHandHeld_nt9dgb7efx6bt` | Package status Disabled |
| Packaged widget | `ULICTekInc.PredatorSenseforHandHeldWidget`, family `ULICTekInc.PredatorSenseforHandHeldWidget_nt9dgb7efx6bt` | Package status Disabled |

`AASHHSvc` hosts AcerCentralService, which starts AcerAgentService and the user-session
AcerHardwareService. Stopping services also removed the other associated service children.
The Desktop and Widget processes were stopped after recording and verifying their package paths.

Stopping processes and services alone was insufficient: Handheld.Widget returned after sign-in.
Its manifest registers `microsoft.gameBarUIExtension` / `Widget` and it was activated with an
AppX server name. The desktop package also registers the `predatorsense:` protocol and execution
alias. Neither package declares a startup-task extension in the inspected manifest.

An elevated `Windows.Management.Deployment.PackageManager.SetPackageStatus(fullName,
PackageStatus.Disabled)` disables activation while retaining installed files. Both installed
packages then reported Disabled/NotAvailable. Explicit IApplicationActivationManager activation
with the no-error-UI option returned `0x80073D5A`, PID 0, for both. No Acer/Predator processes
remained. Calling the same API from the post-reboot non-elevated token failed Access Denied;
elevation was required and verified.

Microsoft documents the [Disabled package status](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packagestatus.disabled)
and the [deployment status flag](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagestatus).

### Keeping the identified targets disabled

On the maintainer's machine, the elevated local scheduled task
`\WSGM-Atlas-OemDisabledState` runs at this user's sign-in and every five minutes while
the interactive user is signed in. It rechecks only the ten exact service names, three task
names and two package identities above. It reapplies Disabled only when needed, including
a replacement package version found under the same identity. It does not disable arbitrary
Acer-named devices, force keyboard changes, install drivers, uninstall apps, or use a global
keyboard blocker. A newly installed, differently named OEM component needs separate review;
an update may briefly reactivate an allowed target before the next five-minute check.

The task runs the admin-protected script
`C:\ProgramData\WSGM\AtlasOemControl\Keep-Acer-OemDisabled.ps1`; its source is tracked in
WSGM as `eng/atlas-oem-disabled-state.ps1`. `last-check.json` records health and any changes.
The first elevated run completed successfully with no changes needed. This is an explicitly
authorized local maintenance task, not an implemented WSGM configuration/UI feature or part
of LibHandheld's device lifecycle.

## Exact keyboard disable and reboot result

Target only the unique, attributed keyboard leaf under the built-in controller:
`HID\VID_0502&PID_F901&MI_02&Col01`. On this unit its current full instance ID is
`HID\VID_0502&PID_F901&MI_02&COL01\7&338D4E88&0&0000`. Instance suffixes are machine-local;
future automation must resolve and verify the exact collection and built-in USB parent,
not hard-code this suffix or select every keyboard.

Normal Disable-PnpDevice returned Not Supported and normal PnPUtil disable refused a required
system device. The maintainer authorized the exact target; PnPUtil `/disable-device <id> /force`
accepted it and explicitly required reboot. Before reboot it still reported problem code 0.
After reboot it reported problem code 22 (disabled), and the maintainer confirmed that the
left hold no longer invoked Win+Tab. No forced reboot was hidden inside the disable operation.

The post-reboot capture verified three short Guide taps, three short QAM taps and three presses
of each volume button, including every release. There were no reader faults, lost events or
injected Win+G. The other HID collections and XInput interface enumerated healthy. This
establishes the tested subset, not every possible control or runtime UI path after disable.
F22 belongs to the disabled leaf and is intentionally unavailable in this configuration.

Before the requested reboot, FSE startup was set to HKCU
`Software\Microsoft\Windows\CurrentVersion\GamingConfiguration\StartupToGamingHome=0`
(DWORD), with machine Winlogon shell `explorer.exe` and no per-user shell override. The value
remained 0 after reboot. FSE is a separate Windows startup preference, not Acer software.

## Recovery and future WSGM management

Record original service start/running states, task enabled states, package status bits, keyboard
problem/status and FSE value existence/type before mutation. Preserve the first snapshot.
The local original snapshots are copied into `C:\ProgramData\WSGM\AtlasOemControl`.

To restore OEM software, first stop and unregister `WSGM-Atlas-OemDisabledState` so it cannot
reapply the disable. Clear only the package Disabled bit with elevated
`PackageManager.ClearPackageStatus(fullName, PackageStatus.Disabled)` when it was not originally
set; retain other status flags. Restore each service/task to its recorded state, not blanket
Automatic/Enabled. Then reopen Predator Sense normally if desired. The local
`artifacts/Restore-Acer-OemSoftware.ps1` consumes these snapshots.

The keyboard is a separate opt-in change: use `pnputil /enable-device <exact id>` and honor
any reboot requirement. The local `artifacts/Enable-Acer-OemKeyboard.cmd` contains the measured
instance. Restoring software does not implicitly re-enable this keyboard or FSE.

Future WSGM OEM management must own these snapshots and bounded disable/restore operations,
track package identity across versions, report reboot-required outcomes honestly, and avoid
claiming that stopped services exclude Game Bar activation. It must keep software policy in WSGM
and native controller mode/input protocols in LibHandheld. These application-management changes
are not yet implemented in WSGM's UI/runtime.

## Remaining acceptance

HardwareVerified remains false. View/Menu attribution and full left-stick leftward travel need
isolated confirmation. Positive motion axes, perceived haptics, held-button disconnect, actual
Windows sleep/wake and live WSGM/Steam UI routing remain unverified. Library suspend/resume and
fixture/build success do not establish those behaviors. The original optional KX driver is blocked
by a revoked certificate; Acer EC power does not depend on it. Nothing in this workflow requires
disabling Defender, driver signing, a shared PS/2 service or Windows keyboard support globally.
