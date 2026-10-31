using LibHandheld.Contracts;
using LibHandheld.Families.Asus;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

// The capability surface: descriptors and the published states.
internal sealed partial class AllyDeviceEngine
{
    private void BuildCapabilitySurface()
    {
        var model = _model ?? throw new InvalidOperationException("No model is selected.");
        List<CapabilityDescriptor> descriptors =
        [
            CapabilityDescriptors.Integer(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit }, model.MinimumWatts,
                    model.MaximumWatts, CapabilityUnit.Watt, read: true, write: true) with
                {
                    PowerPresets = AllyModels.PowerPresets(model),
                    PairedPowerLimitId = CapabilityIds.PowerBoost
                },
            CapabilityDescriptors.Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                new CapabilityDisplay { Key = DisplayKey.BoostPowerLimit }, model.MinimumWatts, model.MaximumWatts,
                CapabilityUnit.Watt, read: true, write: true),
            CapabilityDescriptors.Choice(CapabilityIds.Scenario, CapabilityRole.ScenarioMode,
                new CapabilityDisplay { Key = DisplayKey.PerformanceProfile },
                CapabilityDescriptors.Choices([Scenarios.Silent, Scenarios.Performance, Scenarios.Turbo]),
                read: true, write: true),
            CapabilityDescriptors.Integer(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
                new CapabilityDisplay { Key = DisplayKey.ChargeLimit }, AllyChargeLimitCapability.MinimumPercent,
                AllyChargeLimitCapability.MaximumPercent, CapabilityUnit.Percent, read: true, write: true,
                CapabilityPersistence.DevicePersistent),
            // Write-only: the firmware has no readable "custom curve active" flag.
            CapabilityDescriptors.Choice(CapabilityIds.FanMode, CapabilityRole.FanMode,
                new CapabilityDisplay { Key = DisplayKey.FanMode },
                CapabilityDescriptors.Choices([FanModes.Automatic, FanModes.Custom]), read: false, write: true),
            CapabilityDescriptors.Curve(CapabilityIds.FanCurve, new CapabilityDisplay { Key = DisplayKey.FanCurve },
                AsusFanCurve.DefaultCpuCurve.Take(AsusFanCurve.PointCount).Select(value => (int)value)
                    .ToArray(), read: true),
            CapabilityDescriptors.Integer(CapabilityIds.FanDuty, CapabilityRole.FanDuty,
                new CapabilityDisplay { Key = DisplayKey.FanSpeed }, minimum: 0, maximum: 100,
                CapabilityUnit.Percent, read: false, write: true),
            CapabilityDescriptors.Integer(CapabilityIds.FanReading, CapabilityRole.Telemetry,
                CapabilityDescriptors.Label("CPU fan"), minimum: 0, maximum: 100, CapabilityUnit.Percent,
                read: true, write: false, instance: CapabilityInstances.Cpu),
            CapabilityDescriptors.Integer(CapabilityIds.FanReading, CapabilityRole.Telemetry,
                CapabilityDescriptors.Label("GPU fan"), minimum: 0, maximum: 100, CapabilityUnit.Percent,
                read: true, write: false, instance: CapabilityInstances.Gpu),
            // Aura is write-only (HC and HHD both only write it), so none of these declare a read.
            CapabilityDescriptors.Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                new CapabilityDisplay { Key = DisplayKey.Brightness }, minimum: 0, maximum: 100,
                CapabilityUnit.Percent, read: false, write: true, CapabilityPersistence.Unknown),
            CapabilityDescriptors.Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect,
                new CapabilityDisplay { Key = DisplayKey.LightingEffect }, Effects.Choices, read: false, write: true,
                CapabilityPersistence.Unknown),
            CapabilityDescriptors.Integer(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed,
                new CapabilityDisplay { Key = DisplayKey.LightingEffectSpeed }, minimum: 0, maximum: 100,
                CapabilityUnit.Percent, read: false, write: true, CapabilityPersistence.Unknown),
            CapabilityDescriptors.Color(CapabilityIds.LightingColor, CapabilityInstances.Left,
                CapabilityDescriptors.Label("Left stick ring"), read: false, CapabilityPersistence.Unknown),
            CapabilityDescriptors.Color(CapabilityIds.LightingColor, CapabilityInstances.Right,
                CapabilityDescriptors.Label("Right stick ring"), read: false, CapabilityPersistence.Unknown),
            CapabilityDescriptors.Choice(CapabilityIds.Controller, CapabilityRole.ControllerSource,
                new CapabilityDisplay { Key = DisplayKey.Controller },
                CapabilityDescriptors.Choices(SourceOwnership.Choices), read: true, write: false),
            CapabilityDescriptors.Choice(CapabilityIds.Motion, CapabilityRole.MotionSource,
                new CapabilityDisplay { Key = DisplayKey.Motion }, CapabilityDescriptors.Choices(SourceOwnership.Choices),
                read: true, write: false),
            CapabilityDescriptors.Action(CapabilityIds.Rumble, CapabilityRole.HapticSink,
                new CapabilityDisplay { Key = DisplayKey.Rumble })
        ];

        _descriptorSet = new CapabilityDescriptorSet
        {
            Descriptors = descriptors
        };
    }

    private async ValueTask PublishStatesAsync(CancellationToken cancellationToken)
    {
        if (_descriptorSet is null)
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

            if (descriptor.CapabilityId == CapabilityIds.FanReading)
            {
                _observedValues.Read(descriptor.CapabilityId, CurrentState(descriptor), descriptor.InstanceId);
            }

            var ownership = descriptor.CapabilityId is CapabilityIds.Controller or CapabilityIds.Motion;
            var value = ownership
                ? CurrentState(descriptor)
                : _observedValues.Value(descriptor.CapabilityId, descriptor.InstanceId);
            var owned = service.State is DeviceServiceState.Owned;
            await _publications.PublishCapabilityStateAsync(ProviderControls.State(descriptor.CapabilityId, owned,
                value,
                descriptor.CapabilityId == CapabilityIds.FanReading ? _fans?.TelemetryObservedAt
                : ownership ? service.ChangedAt
                : _observedValues.Time(descriptor.CapabilityId, descriptor.InstanceId),
                owned ? null : service.Reason ?? DeviceServiceLifecycle.ReasonFor(service.State),
                descriptor.InstanceId), cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        switch (descriptor.CapabilityId)
        {
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
}

internal static class CapabilityInstances
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Left = "left";
    public const string Right = "right";
}

internal static class Effects
{
    public const string Solid = "solid";
    public const string Breathing = "breathing";
    public const string ColorCycle = "color-cycle";
    public const string Rainbow = "rainbow";

    /// <summary>The effect choices in menu order, labelled as the shared choice labels except "Colour cycle".</summary>
    public static IReadOnlyList<CapabilityChoice> Choices { get; } =
    [
        .. CapabilityDescriptors.Choices([Solid, Breathing]),
        new CapabilityChoice(ColorCycle, CapabilityDescriptors.Label("Colour cycle")),
        .. CapabilityDescriptors.Choices([Rainbow])
    ];

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
