# Lifetime and recovery

Create one owner for the detected exact model. HandheldDevice serializes its commands and native
transitions. The host coordinates competing applications and machine-wide ownership.

| Operation                      | Contract                                                                        |
| ------------------------------ | ------------------------------------------------------------------------------- |
| `StartAsync`                   | Acquires native services once per instance; returns Passive, Active or Degraded |
| `SuspendAsync`                 | Quiesces native services and stops active publications                          |
| `ResumeAsync`                  | Reacquires the same identity and owner after suspension                         |
| `SetControllerManagementAsync` | Changes physical acquisition independently of other controls                    |
| `ReleaseControllerAsync`       | Releases acquisition and restores its native baseline                           |
| `StopAsync`                    | Performs terminal restoration; repeated calls retain the first stop result      |
| `DisposeAsync`                 | Joins owned work and releases handles; does not replace Stop                    |

Start is admitted once. Create a new instance after terminal stop. An active flag controls dispatch
and publication; consumers do not supply lifecycle generations.

Before controller handoff or suspend, stop previews, neutralize forwarding and end consumer output.
Invoke transitions with a bounded Deadline. Resume preserves the selected model; it does not
redetect or switch hardware. The host owns virtual-target selection and OEM action policy.

## Cancellation and restoration

`Deadline.After` uses an active-time budget. Cancellation requests cooperation and cannot establish
that a dispatched native write did nothing. TimedOut and Indeterminate require resolution or an
explicit fresh action; never automatically replay them.

Call Stop in finally, then dispose. Clean means restoration was confirmed; Unverified means cleanup
lacks full independent confirmation; Failed means a restore failed. Record the stop reason
separately from command outcomes.

The caller supplies persistent storage. One `recovery.json` holds service identities and captured
originals. It preserves the first known original until restoration or retirement. Storage failures
are diagnosed without locking otherwise writable controls. Old family files are not interpreted as
the shared journal. Keep the directory stable and protect it from concurrent writers; do not erase
recovery evidence to hide a failed restore. The native family distinguishes temporary state from
durable user settings.
