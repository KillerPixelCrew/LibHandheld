# Getting started

## Build and reference

Use the .NET 10 SDK and an x64 Windows application targeting `net10.0-windows10.0.19041.0` or a
compatible newer Windows target. This guide uses a source project reference; it does not assume a
published NuGet package.

```powershell
git clone https://github.com/KillerPixelCrew/LibHandheld.git
cd LibHandheld
dotnet build LibHandheld.slnx -c Release
```

```xml
<ItemGroup>
  <ProjectReference Include="../LibHandheld/src/LibHandheld/LibHandheld.csproj" />
</ItemGroup>
```

## One complete device cycle

Read identity once, detect an exact model, assign an observer if needed, and acquire native
services. An unknown machine remains passive. Create checks that the supplied definition matches
that identity.

```csharp
using LibHandheld;
using LibHandheld.Contracts;

var identity = HandheldDevice.ReadIdentity();
var definition = HandheldDevice.Detect(identity);
if (definition is null)
{
    Console.WriteLine("No exact supported handheld matched.");
    return;
}

var directory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyApplication", "Devices", definition.FamilyId);
var resources = new HandheldResources(); // Supply deployed native helper paths where required.
await using var device = HandheldDevice.Create(definition, identity, directory,
    diagnostics: entry => Console.WriteLine($"{entry.Scope}: {entry.Message}"),
    resources: resources);
try
{
    var start = await device.StartAsync(controllerManagementEnabled: false);
    Console.WriteLine($"{definition.Name}: {start.State}");
    var power = device.Controls.Find(CapabilityRole.PowerSustainedLimit);
    if (start.State != DeviceOperationalState.Passive && power?.Descriptor.SupportsWrite == true)
    {
        // Replace 20 with an explicit user choice inside this model's current bounds.
        var result = await device.SetTdpAsync(20);
        Console.WriteLine($"Power request: {result.Outcome}");
    }
}
finally
{
    var stop = await device.StopAsync(Deadline.After(TimeSpan.FromSeconds(5)));
    Console.WriteLine($"Restoration: {stop.Status}; {stop.Reason?.Detail}");
}
```

This example writes hardware when run. Compile examples without running them for documentation
checks. Detect and Create acquire no mutable hardware; Start does. Stop restores hardware, while
Dispose releases handles. Check lifecycle and command results independently.

Assign the borrowed `IHandheldObserver` before Start for publications and physical input. Enable
controller management explicitly when acquisition is needed. Virtual targets and HidHide are host
policy. Deploy [native dependencies](devices.md) before offering their controls.
