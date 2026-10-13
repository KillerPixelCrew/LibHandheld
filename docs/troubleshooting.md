# Troubleshooting

| Symptom                          | Inspect and act                                                                               |
| -------------------------------- | --------------------------------------------------------------------------------------------- |
| Detect returns null              | Report exact board/product, CPU, BIOS and USB metadata; remain passive                        |
| Create rejects a definition      | Use Detect's result with the same snapshot                                                    |
| Passive/Degraded startup         | Check reason, diagnostics, required helpers, permissions and competing owners                 |
| Missing control                  | Inspect live descriptors and native provider; do not substitute a different firmware protocol |
| Unknown value with write support | Offer the available writable control without inventing readback                               |
| Ambiguous fan or zone            | Select the descriptor's intended logical instance                                             |
| TimedOut/Indeterminate write     | Retain uncertainty; resolve state or require a fresh explicit action                          |
| No controller reports            | Check acquisition, physical-interface publications and host forwarding independently          |
| Haptics stop while input works   | Inspect output capabilities; explicitly recover output ownership                              |
| Unverified/Failed stop           | Report restoration separately and retain recovery evidence                                    |

Supply Action<DeviceLogEntry> diagnostics and use GetDiagnosticsAsync for a detached dictionary. Log
transitions and command outcomes, not every report. Include model, firmware, provider/helper
versions and the affected operation in reports; avoid exposing serial numbers or personal paths.
Builds, fixtures and authored artwork do not prove physical behavior.
