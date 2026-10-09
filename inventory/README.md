# Device inventory

Research date: 2026-10-09. Target: **every device supported by HC or HHD**, for an independently
implemented Windows-first .NET library. Source priority is **HC > HHD > OpenGamepadUI**.

## Pinned sources

| Source                                                                                                            | Snapshot                                                        | Role                                               |
| ----------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- | -------------------------------------------------- |
| [Handheld Companion](https://github.com/Valkirie/HandheldCompanion/tree/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e) | 1.3.3.1, `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`, 2026-10-04 | Primary Windows device facts                       |
| [Handheld Daemon](https://github.com/hhd-dev/hhd/tree/a87fb308f9f14d4a78ef3cb0b8515827459f8466)                   | 4.1.12, `a87fb308f9f14d4a78ef3cb0b8515827459f8466`, 2026-09-30  | Additional target devices and secondary mechanisms |
| [OpenGamepadUI](https://github.com/ShadowBlip/OpenGamepadUI/tree/0db5812f21e98d0a5ac70bb1b5a674f2f369c22c)        | 0.46.1, `0db5812f21e98d0a5ac70bb1b5a674f2f369c22c`              | Tertiary platform and service facts                |
| [InputPlumber](https://github.com/ShadowBlip/InputPlumber/tree/ea60d873cca17edd1cb655ede26f557108135252)          | 0.81.0, `ea60d873cca17edd1cb655ede26f557108135252`              | OpenGamepadUI input-service research snapshot      |
| [PowerStation](https://github.com/ShadowBlip/PowerStation/tree/b7f6def5b5e251fc19b15251723963aabf5f442d)          | 0.8.3, `b7f6def5b5e251fc19b15251723963aabf5f442d`               | OpenGamepadUI power-service boundary research      |

OpenGamepadUI does not pin the fetched InputPlumber/PowerStation releases in the inspected packaging
rules. Those dependency commits are research snapshots, not a claim about the version shipped by
every OpenGamepadUI installation. Older local HC 1.3.1.6 decompilation is supplementary reference;
the current public HC snapshot above has priority within HC.

## Coverage

- **HC:** 90 records, including 89 factory classes and a default fallback; all 53 JSON device
  profiles, 104 device source files and 12 shared base classes accounted for.
- **HHD:** 111 records, including 84 exact controller profiles, four ASUS ranges, power-only models
  and ranges, controller/CPU fallbacks, secondary Aura controls and auxiliary touchscreen profiles.
  All 56 unified board/product power rows are accounted for.
- **OpenGamepadUI:** 88 tertiary provenance records: 25 platform presets, an AYANEO 3 packaging rule
  and 62 InputPlumber handheld/convertible candidates. All 80 InputPlumber profile files are
  accounted for; 18 peripheral, generic or policy profiles are separately excluded.
- **Combined view:** 201 HC/HHD source-specific target records. Tertiary-only candidates stay
  outside the HC/HHD target and remain available in the tertiary inventory.

These counts are source records, not a deduplicated count of marketed devices. Fallback ranges,
secondary controls, aliases, CPU/board variants and partial support must remain visible. The
machine-readable `entryKind` identifies HHD scope and HC fallback rows.

## Files

| File                                                                                                           | Purpose                                                                         |
| -------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| [devices.json](devices.json)                                                                                   | Complete HC/HHD target records, source references and preferred fact candidates |
| [devices.csv](devices.csv)                                                                                     | Spreadsheet-friendly identity and feature summary                               |
| [differences.json](differences.json)                                                                           | Source-status differences and ambiguous variant associations to review          |
| [sources/hc.md](sources/hc.md), [sources/hc.json](sources/hc.json)                                             | HC factory selectors, profiles, inheritance, limits, quirks and source gaps     |
| [sources/hhd.md](sources/hhd.md), [sources/hhd.json](sources/hhd.json)                                         | HHD controller/power detectors, configurations and Linux dependencies           |
| [sources/opengamepadui.md](sources/opengamepadui.md), [sources/opengamepadui.json](sources/opengamepadui.json) | Direct OpenGamepadUI facts and separately attributed service evidence           |
| [reconcile.py](reconcile.py)                                                                                   | Rebuild the combined JSON/CSV view from the checked-in source inventories       |

The full source inventories retain numerical limits, firmware predicates, controller selectors,
axis matrices, configuration fields and source uncertainties. Follow `sourceRecordId` to the
corresponding source JSON record for those details.

## Evidence and reconciliation

- **implemented** means a concrete execution path exists in that source.
- **declared** means metadata, configuration or a mapping declares the feature.
- **unsupported** means the source explicitly removes or refuses that feature.
- **unknown** means the evidence is insufficient.

These statuses describe upstream source evidence. LibHandheld implementation status is currently
`planned`; source support is separate from hardware validation.

Related records are linked by name or selector-text overlap. That is a research association, not
proof that WMI baseboard, DMI product, firmware and CPU selectors are interchangeable. Multiple
possible primary variants stay `needs-variant-resolution`. The combined view applies source
priority to available fact candidates, retaining all lower-priority observations. Unknown HC facts
may use HHD and then tertiary evidence.

The differences file contains reconciliation candidates. A Linux implementation versus Windows
metadata, or two different selector scopes, is not automatically a contradictory hardware fact.
For an established conflict on the same fact and variant, use HC, then HHD, then OpenGamepadUI.

Linux sysfs, evdev, IIO, hidraw, D-Bus and kernel-module dependencies require Windows equivalents.
Record the actual Windows transport and dependencies during implementation; Linux support alone
does not establish that path.

## Rebuild

```powershell
py -3 inventory/reconcile.py
```

The script operates on the saved JSON records and does not inspect hardware or execute reference
code. Source refreshes must update snapshot pins and evidence before rebuilding the combined view.
