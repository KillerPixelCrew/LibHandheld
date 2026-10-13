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

The native families cover AYANEO, GPD, Legion Go, MSI Claw, ONEXPLAYER/AOKZOE, ROG Ally, Valve,
Zotac and additional exact models under Generic. Generic is a source grouping, not a CPU fallback.
The catalog has 112 definitions and 106 glyph profiles. Only Claw 8 A2VM and Xbox Ally X are
hardware-tested.

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
