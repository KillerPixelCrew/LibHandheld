using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Services;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

// The capability surface: overlay sections, descriptors and the published states.
internal sealed partial class AllyDeviceEngine
{
    private void BuildCapabilitySurface()
    {
        var model = _model ?? throw new InvalidOperationException("No model is selected.");
        List<CapabilityDescriptor> descriptors =
        [
            Integer(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit, DisplayKey.SustainedPowerLimit,
                    model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, SectionIds.Power,
                    CategoryIds.Limits, 0) with
                {
                    PowerPresets = AllyModels.PowerPresets(model),
                    PairedPowerLimitId = CapabilityIds.PowerBoost
                },
            Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit, DisplayKey.BoostPowerLimit,
                model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, SectionIds.Power,
                CategoryIds.Limits, 1),
            Choice(CapabilityIds.Scenario, CapabilityRole.ScenarioMode, DisplayKey.PerformanceProfile,
                [Scenarios.Silent, Scenarios.Performance, Scenarios.Turbo], true, true, SectionIds.Power,
                CategoryIds.Limits, 2),
            Integer(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit, DisplayKey.ChargeLimit,
                    AllyChargeLimitCapability.MinimumPercent, AllyChargeLimitCapability.MaximumPercent,
                    CapabilityUnit.Percent, true, SectionIds.Power, CategoryIds.Charging, 0) with
                {
                    Persistence = CapabilityPersistence.DevicePersistent
                },
            // Write-only: the firmware has no readable "custom curve active" flag.
            Choice(CapabilityIds.FanMode, CapabilityRole.FanMode, DisplayKey.FanMode,
                [FanModes.Automatic, FanModes.Custom], false, true, SectionIds.Power, CategoryIds.Fans, 0),
            new()
            {
                CapabilityId = CapabilityIds.FanCurve,
                Role = CapabilityRole.FanCurve,


                ValueKind = CapabilityValueKind.Curve,
                Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
                Minimum = 0,
                Maximum = 100,
                Unit = CapabilityUnit.Percent,
                SupportsRead = true,
                SupportsWrite = true,
                Persistence = CapabilityPersistence.Volatile
            },
            Integer("fan.duty", CapabilityRole.FanDuty, DisplayKey.FanSpeed, 0, 100, CapabilityUnit.Percent,
                    true, SectionIds.Power, CategoryIds.Fans, 2) with
                {
                    SupportsRead = false
                },
            Integer(CapabilityIds.FanReading, CapabilityRole.Telemetry, DisplayKey.Custom, 0, 100,
                    CapabilityUnit.Percent, false, SectionIds.Info, CategoryIds.Readings, 0,
                    CapabilityInstances.Cpu) with
                {
                    Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "CPU fan" }
                },
            Integer(CapabilityIds.FanReading, CapabilityRole.Telemetry, DisplayKey.Custom, 0, 100,
                    CapabilityUnit.Percent, false, SectionIds.Info, CategoryIds.Readings, 1,
                    CapabilityInstances.Gpu) with
                {
                    Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "GPU fan" }
                },
            // Aura is write-only (HC and HHD both only write it), so none of these declare a read.
            Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness, DisplayKey.Brightness, 0, 100,
                    CapabilityUnit.Percent, true, SectionIds.Lighting, null, 0) with
                {
                    SupportsRead = false,
                    Persistence = CapabilityPersistence.Unknown
                },
            Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect, DisplayKey.LightingEffect,
                    [Effects.Solid, Effects.Breathing, Effects.ColorCycle, Effects.Rainbow], false, true,
                    SectionIds.Lighting, null, 1) with
                {
                    Persistence = CapabilityPersistence.Unknown
                },
            Integer(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed, DisplayKey.LightingEffectSpeed,
                    0, 100, CapabilityUnit.Percent, true, SectionIds.Lighting, null, 2) with
                {
                    SupportsRead = false,
                    Persistence = CapabilityPersistence.Unknown
                },
            Color(CapabilityInstances.Left, "Left stick ring", 0),
            Color(CapabilityInstances.Right, "Right stick ring", 1),
            Choice(CapabilityIds.Controller, CapabilityRole.ControllerSource, DisplayKey.Controller,
                SourceOwnership.Choices, true, false, SectionIds.Info, CategoryIds.Ownership, 0),
            Choice(CapabilityIds.Motion, CapabilityRole.MotionSource, DisplayKey.Motion, SourceOwnership.Choices, true,
                false, SectionIds.Info, CategoryIds.Ownership, 1),
            new()
            {
                CapabilityId = CapabilityIds.Rumble,
                Role = CapabilityRole.HapticSink,


                ValueKind = CapabilityValueKind.None,
                Display = new CapabilityDisplay { Key = DisplayKey.Rumble },
                SupportsAction = true,
                Persistence = CapabilityPersistence.Volatile
            }
        ];

        _descriptorSet = new CapabilityDescriptorSet
        {
            Descriptors = descriptors
        };
    }

    private async ValueTask PublishStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _descriptorSet is null)
        {
            return;
        }

        foreach (var descriptor in _descriptorSet.Descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ServiceFor(descriptor.CapabilityId) is not { } service)
            {
                continue;
            }

            _observedValues.Read(descriptor.CapabilityId, descriptor.SupportsRead ? CurrentState(descriptor) : null,
                descriptor.InstanceId);
            var value = _observedValues.Value(descriptor.CapabilityId, descriptor.InstanceId);
            await _host.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId,
                InstanceId = descriptor.InstanceId,
                Available = service.State is DeviceServiceState.Owned,
                Reason = service.State is DeviceServiceState.Owned
                    ? null
                    : service.Reason ?? DeviceServiceLifecycle.ReasonFor(service.State),
                ObservedValue = value,
                Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = value is null ? null : _observedValues.Time(descriptor.CapabilityId, descriptor.InstanceId)
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        switch (descriptor.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
                return _power?.LastObserved?.Sustained is { } sustained && InRange(sustained, descriptor)
                    ? CapabilityValue.Integer(sustained)
                    : null;
            case CapabilityIds.PowerBoost:
                // The boost descriptor drives SPPT and FPPT together; SPPT stands for the pair.
                return _power?.LastObserved?.Slow is { } slow && InRange(slow, descriptor)
                    ? CapabilityValue.Integer(slow)
                    : null;
            case CapabilityIds.Scenario:
                return _power?.LastObserved?.Mode is { } mode && AllyModels.ScenarioName(mode) is { } name
                    ? CapabilityValue.Choice(name)
                    : null;
            case CapabilityIds.ChargeLimit:
                return _charge?.LastObserved is { } percent && InRange(percent, descriptor)
                    ? CapabilityValue.Integer(percent)
                    : null;
            case CapabilityIds.FanCurve:
                return _fans?.LastObserved?.Cpu is { } curve
                    ? CapabilityValue.Curve(AllyFanCapability.Decode(curve))
                    : null;
            case CapabilityIds.FanReading:
                var reading = descriptor.InstanceId == CapabilityInstances.Cpu
                    ? _fans?.LastFans.Cpu
                    : _fans?.LastFans.Gpu;
                return reading is { } fan && InRange(fan, descriptor) ? CapabilityValue.Integer(fan) : null;
            case CapabilityIds.Rumble:
                return CapabilityValue.None();
            case CapabilityIds.Controller:
                return CapabilityValue.Choice(DeviceServiceLifecycle.Ownership(_controller?.State));
            case CapabilityIds.Motion:
                return CapabilityValue.Choice(DeviceServiceLifecycle.Ownership(_motion?.State));
            default:
                return null;
        }
    }

    private static bool InRange(int value, CapabilityDescriptor descriptor)
    {
        return value >= descriptor.Minimum && value <= descriptor.Maximum;
    }

    private static CapabilityDescriptor Integer(
        string id,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool writable,
        string section,
        string? category,
        int order,
        string? instance = null)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,


            ValueKind = CapabilityValueKind.Integer,
            Display = display is DisplayKey.Custom
                ? new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = id }
                : new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = unit,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Choice(
        string id,
        CapabilityRole role,
        DisplayKey display,
        IReadOnlyList<string> choices,
        bool readable,
        bool writable,
        string section,
        string? category,
        int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,


            ValueKind = CapabilityValueKind.Choice,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = readable,
            SupportsWrite = writable,
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(choice,
                    new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = Label(choice) }))
            ],
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Color(string instance, string label, int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor,
            InstanceId = instance,
            Role = CapabilityRole.LightingZoneColor,


            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = label },
            SupportsRead = false,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Unknown
        };
    }

    private static string Label(string choice)
    {
        return choice switch
        {
            Scenarios.Silent => "Silent",
            Scenarios.Performance => "Performance",
            Scenarios.Turbo => "Turbo",
            FanModes.Automatic => "Automatic",
            FanModes.Custom => "Custom",
            Effects.Solid => "Solid",
            Effects.Breathing => "Breathing",
            Effects.ColorCycle => "Colour cycle",
            Effects.Rainbow => "Rainbow",
            SourceOwnership.Device => "Device",
            SourceOwnership.Managed => "Plugin",
            SourceOwnership.Unavailable => "Unavailable",
            _ => choice
        };
    }
}

internal static class CapabilityInstances
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Left = "left";
    public const string Right = "right";
}

internal static class SectionIds
{
    public const string Power = DeviceSections.PowerId;
    public const string Lighting = DeviceSections.RgbId;
    public const string Info = DeviceSections.InfoId;
}

internal static class CategoryIds
{
    public const string Limits = "limits";
    public const string Charging = "charging";
    public const string Fans = "fans";
    public const string Zones = "zones";
    public const string Ownership = "ownership";
    public const string Readings = "readings";
}

internal static class Effects
{
    public const string Solid = "solid";
    public const string Breathing = "breathing";
    public const string ColorCycle = "color-cycle";
    public const string Rainbow = "rainbow";

    public static AuraEffect Parse(string value)
    {
        return value switch
        {
            Breathing => AuraEffect.Breathing,
            ColorCycle => AuraEffect.ColorCycle,
            Rainbow => AuraEffect.Rainbow,
            _ => AuraEffect.Solid
        };
    }
}
