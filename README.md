# LibHandheld

**A .NET library for Windows handheld hardware.**

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](src/LibHandheld/LibHandheld.csproj)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)](docs/devices.md)
[![MIT license](https://img.shields.io/badge/License-MIT-green)](LICENSE)

Detect a handheld and control its power, fans, battery and lighting through one API. Read physical
controller input and motion, send haptic feedback, and receive the device's OEM button events.
LibHandheld handles the model-specific protocols; your application supplies its profiles, UI and
controller routing.

[Getting started](docs/getting-started.md) · [Documentation](docs/README.md) ·
[API reference](docs/reference.md) · [Contributing](CONTRIBUTING.md)

## Features

| Area      | What the library provides                                                                       |
| --------- | ----------------------------------------------------------------------------------------------- |
| Detection | Exact model matching from Windows board, product, processor and USB identity                    |
| Power     | Sustained, slow, fast and peak power limits; supported processor settings and performance modes |
| Cooling   | Fan modes, duty, target RPM, firmware curves and measured RPM                                   |
| Battery   | Supported charge limits, protection modes, bypass and native measurements                       |
| Lighting  | Model-specific zones, colours, brightness, effects and secondary-display controls               |
| Input     | Canonical controller reports, gyro/accelerometer data, touch and OEM button events              |
| Haptics   | Physical rumble and supported native actuator feedback                                          |
| Artwork   | Authored device glyphs and their attribution notices                                            |
| Lifecycle | Acquisition, suspend/resume, physical-controller handoff and native state restoration           |

Each model publishes its available controls, legal values and instances. Applications use that
metadata rather than assuming every handheld has the same hardware or firmware features.

## Device support

The library contains **113 exact model definitions** across Acer, AYANEO, AYN, GPD, Legion Go, MSI
Claw, ONEXPLAYER/AOKZOE, ROG Ally, Valve, Zotac and other supported models, with **107 glyph
profiles**.

The MSI Claw 8 A2VM and ROG Xbox Ally X have attended hardware evidence. The Acer Predator Atlas 8
also has autonomous on-device power, charging, lighting and lifecycle validation, with remaining
physical acceptance recorded in its [provenance](src/LibHandheld/Families/Acer/PROVENANCE.md). Other
definitions still need testing on the corresponding hardware.

Use `HandheldDevice.Definitions` to inspect the catalog and `Detect` to match the current machine.
Unknown identities return no match. See [devices and dependencies](docs/devices.md) for matching,
native helpers and availability.

## Getting started

Requirements: **.NET 10**, **Windows 10 build 19041 or newer**, and an **x64 application**. Native
helpers and permissions depend on the selected device.

```powershell
git clone https://github.com/KillerPixelCrew/LibHandheld.git
cd LibHandheld
dotnet build LibHandheld.slnx -c Release
```

Add a project reference to `src/LibHandheld/LibHandheld.csproj` in your application, then create one
owner for the detected device:

```csharp
using LibHandheld;
using LibHandheld.Contracts;

var identity = HandheldDevice.ReadIdentity();
var model = HandheldDevice.Detect(identity);
if (model is null)
{
    Console.WriteLine("No supported handheld detected.");
    return;
}

var stateDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyApplication", "Devices", model.FamilyId);

await using var device = HandheldDevice.Create(model, identity, stateDirectory);
try
{
    var result = await device.StartAsync(controllerManagementEnabled: false);
    Console.WriteLine($"{model.Name}: {result.State}");

    foreach (var control in device.Controls.Controls)
    {
        Console.WriteLine($"{control.Role}: {control.Descriptor.SupportsWrite}");
    }
}
finally
{
    var result = await device.StopAsync(Deadline.After(TimeSpan.FromSeconds(5)));
    Console.WriteLine($"Restoration: {result.Status}");
}
```

After startup, use semantic methods such as `SetTdpAsync`, `SetFanModeAsync` and
`SetChargeLimitAsync` with values from the published descriptors. Detection and construction do not
acquire hardware. Call `StopAsync` for restoration before disposal, and inspect command outcomes
before accepting or repeating a request.

For physical input, assign an `IHandheldObserver` and enable controller management. The
[getting-started guide](docs/getting-started.md) covers helper configuration and writes; the
[input guide](docs/input.md) covers reports, haptics and glyphs.

## Documentation

| Guide                                       | Read it for                                                 |
| ------------------------------------------- | ----------------------------------------------------------- |
| [Devices and dependencies](docs/devices.md) | Identity matching, permissions and native providers         |
| [Controls](docs/controls.md)                | Power, cooling, charging, lighting and command outcomes     |
| [Input, haptics and glyphs](docs/input.md)  | Observer callbacks, coordinates, output and artwork         |
| [Lifetime and recovery](docs/lifecycle.md)  | Suspend/resume, handoff, cancellation and cleanup           |
| [Troubleshooting](docs/troubleshooting.md)  | Missing controls, unavailable services and uncertain writes |
| [API reference](docs/reference.md)          | Public types, methods, properties and enum values           |

## Sources and license

Device information was collected from **public and open sources**, including
[HHD](https://github.com/hhd-dev/hhd),
[HandheldCompanion](https://github.com/Valkirie/HandheldCompanion),
[InputPlumber](https://github.com/ShadowBlip/InputPlumber) and
[OpenGamepadUI](https://github.com/ShadowBlip/OpenGamepadUI). Implementation attribution is retained
in [PROVENANCE.md](PROVENANCE.md) and the family notices.

LibHandheld is [MIT licensed](LICENSE). Embedded artwork and native modules retain their own
licenses and notices. See [Contributing](CONTRIBUTING.md) to add a device or improve a protocol.

Used by [WSGM](https://github.com/KillerPixelCrew/WSGM). For graphics driver controls, see
[LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract); for generic Windows
controls, see [WindowsDeviceControl](https://github.com/KillerPixelCrew/windows-device-control).
