# Devices and dependencies

`HandheldDevice.Definitions` is the model catalog. Definitions expose `Id`, `FamilyId`, `Name`,
`GlyphProfileId`, `HasController`, `HardwareAccess`, `HardwareVerified` and `DeclaredRoles`.

```csharp
foreach (var model in HandheldDevice.Definitions)
{
    Console.WriteLine($"{model.Id}: {model.Name}; {model.HardwareAccess}");
}
```

ReadIdentity captures read-only Windows board/product, BIOS, processor and USB metadata. Missing
EC/MCU facts remain unknown; detection does not probe feature reports or privileged drivers. Detect
matches that snapshot. A friendly model name or CPU vendor cannot override the native identity.

The native families cover Acer, AYANEO, GPD, Legion Go, MSI Claw, ONEXPLAYER/AOKZOE, ROG Ally,
Valve, Zotac and additional exact models under Generic. Generic is a source grouping, not a CPU
fallback. The catalog has 113 definitions and 107 glyph profiles. Claw 8 A2VM and Xbox Ally X have
attended hardware evidence. Atlas 8 also has the autonomous on-device results described below.

Acer Predator Atlas 8 PA08-I51 supplies exact system/board detection, XInput/controller rumble,
Guide/QAM/M1/M2 input, native EC power and performance modes, two firmware-discovered system fans
with RPM, optimized charging, Windows motion and volatile controller lighting. Eight individual ring segments have attended physical mapping; shared color retains the unassigned ninth wire slot. Power, charge and
brightness write/readback/restoration passed on BIOS V1.03, controller release 0100. Fan original
behavior remains unknown; its Auto fallback is reported unverified. Optional KX tuning is blocked by
Windows security on this unit and is independent of EC power. HardwareVerified remains false; see
[Acer provenance](../src/LibHandheld/Families/Acer/PROVENANCE.md) for exact boundaries and the
remaining physical acceptance.

DeclaredRoles lists possible model services, including optional accessories. Live descriptors and
availability determine usable controls and instances; do not expose every declared role
unconditionally.

## Providers and permissions

| Access flag | Requirement                                         | Host responsibility                                                           |
| ----------- | --------------------------------------------------- | ----------------------------------------------------------------------------- |
| `PawnIo`    | Installed PawnIO provider for signed native modules | Deploy the reviewed provider; embedded module notices remain with the library |
| `IntelKx`   | Supported pinned Intel power helper                 | Supply `HandheldResources.IntelKxPath`                                        |
| `InpOut`    | Supported Steam Deck EC/MMIO helper                 | Supply `HandheldResources.InpOutPath`                                         |
| `None`      | No privileged helper declared by this flag          | HID/WMI access still depends on physical interfaces and permissions           |

Flags combine. Inspect the exact selected definition rather than choosing by CPU alone. The host
chooses required elevation, validates native payload provenance and installs dependencies.
LibHandheld does not install drivers. Missing providers and denied access appear in lifecycle
outcomes and diagnostics.

USB/IP, VIIPER and HidHide belong to a consumer's virtual-controller pipeline. They are not required
to enumerate definitions or construct the owner. Physical HID/controller acquisition can still be
blocked by a competing application.
