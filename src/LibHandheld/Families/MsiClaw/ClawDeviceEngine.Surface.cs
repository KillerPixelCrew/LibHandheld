using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

// The capability surface: descriptors, published states and OEM controls.
internal sealed partial class ClawDeviceEngine
{
    /// <summary>The Claw's descriptor set, one static fact for every unit of a model.</summary>
    /// <remarks>Titles, icons and grouping are WSGM-owned vocabulary; the plugin declares only the capabilities.</remarks>
    private void BuildCapabilitySurface()
    {
        if (_power is null || _chargeLimit is null || _fans is null || _telemetry is null
            || _lighting is null
            || _motion is null || _controller is null)
        {
            throw new InvalidOperationException("Services must exist before descriptors are built.");
        }

        IReadOnlyList<CapabilityDescriptor> descriptors =
        [
            // PL1 shares PL2's ceiling: on the A2VM that is 37 W, not the 30 W it ships at.
            CapabilityDescriptors.Integer(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit }, Model.MinimumWatts,
                    Model.MaximumWatts, CapabilityUnit.Watt, read: true, write: true) with
                {
                    PowerPresets = Model.PowerPresets,
                    PairedPowerLimitId = CapabilityIds.PowerBoost
                },
            CapabilityDescriptors.Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                new CapabilityDisplay { Key = DisplayKey.BoostPowerLimit }, Model.MinimumWatts, Model.MaximumWatts,
                CapabilityUnit.Watt, read: true, write: true),
            // HC's BatteryBypassStep: the limit is 60, 80 or 100 percent.
            CapabilityDescriptors.Integer(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
                new CapabilityDisplay { Key = DisplayKey.ChargeLimit }, ClawChargeLimitCapability.MinimumPercent,
                ClawChargeLimitCapability.MaximumPercent, CapabilityUnit.Percent, read: true, write: true,
                CapabilityPersistence.DevicePersistent, step: ClawChargeLimitCapability.StepPercent),
            CapabilityDescriptors.Boolean(CapabilityIds.ChargeProtection, CapabilityRole.ChargeProtectionMode,
                CapabilityDescriptors.Label("Battery Master"), read: false,
                persistence: CapabilityPersistence.DevicePersistent),
            CapabilityDescriptors.Choice(CapabilityIds.Scenario, CapabilityRole.ScenarioMode,
                new CapabilityDisplay { Key = DisplayKey.PerformanceProfile },
                CapabilityDescriptors.Choices(["comfort", "green", "eco", "user", "sport", "inactive"]),
                read: true, write: true),
            CapabilityDescriptors.Choice(CapabilityIds.FanMode, CapabilityRole.FanMode,
                new CapabilityDisplay { Key = DisplayKey.FanMode },
                CapabilityDescriptors.Choices(["automatic", "custom", "full-speed"]), read: true, write: true),
            FanCurveDescriptor(),
            CapabilityDescriptors.Integer(CapabilityIds.FanDuty, CapabilityRole.FanDuty,
                new CapabilityDisplay { Key = DisplayKey.FanSpeed }, minimum: 0, maximum: 100,
                CapabilityUnit.Percent, read: false, write: true),
            CapabilityDescriptors.Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                new CapabilityDisplay { Key = DisplayKey.Brightness }, minimum: 0, maximum: 100,
                CapabilityUnit.Percent, read: true, write: true, CapabilityPersistence.DevicePersistent),
            LightingColorDescriptor(CapabilityInstances.LeftRing, "Left ring"),
            LightingColorDescriptor(CapabilityInstances.RightRing, "Right ring"),
            LightingColorDescriptor(CapabilityInstances.Buttons, "Buttons"),
            CapabilityDescriptors.Ownership(CapabilityIds.Controller, CapabilityRole.ControllerSource,
                new CapabilityDisplay { Key = DisplayKey.Controller }),
            CapabilityDescriptors.Ownership(CapabilityIds.Motion, CapabilityRole.MotionSource,
                new CapabilityDisplay { Key = DisplayKey.Motion }),
            // A sink offers the invoke as its one operation: rumble is written to it, never read back.
            CapabilityDescriptors.Action(CapabilityIds.Rumble, CapabilityRole.HapticSink,
                new CapabilityDisplay { Key = DisplayKey.Rumble }),
            // Readings, not controls. They left the Power page because a person opens it to change
            // how the device behaves, not to watch numbers; the CPU temperature stays published
            // because the fan-curve editor draws the live temperature against the curve.
            CapabilityDescriptors.Integer(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                new CapabilityDisplay { Key = DisplayKey.CpuTemperature }, minimum: 0, maximum: 110,
                CapabilityUnit.Celsius, read: true, write: false),
            // Both fans are still measured separately even though they are driven together: one
            // failing fan is exactly the fault this page exists to make visible.
            CapabilityDescriptors.Integer(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                new CapabilityDisplay { Key = DisplayKey.FanLeft }, minimum: 0, maximum: 10_000, CapabilityUnit.Rpm,
                read: true, write: false, instance: CapabilityInstances.Left),
            CapabilityDescriptors.Integer(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                new CapabilityDisplay { Key = DisplayKey.FanRight }, minimum: 0, maximum: 10_000, CapabilityUnit.Rpm,
                read: true, write: false, instance: CapabilityInstances.Right)
        ];

        _descriptorSet = new CapabilityDescriptorSet
        {
            Descriptors = descriptors
        };
    }

    /// <summary>The one fan curve, applied to both channels.</summary>
    /// <remarks>
    ///     One capability rather than a left and a right instance. The A2VM's fans share a heatsink and
    ///     the firmware ramps them together, so two independently authored curves described a machine
    ///     that does not exist and made the user set the same thing twice.
    ///     <para>
    ///         The 0-100 bounds are declared, not implied: they are what the firmware accepts for a duty
    ///         byte, and WSGM's curve editor needs a stated range to draw an axis and clamp a drag against.
    ///         An undeclared bound means "no limit" to the router, which would let the editor offer values
    ///         the write would then refuse.
    ///     </para>
    /// </remarks>
    private static CapabilityDescriptor FanCurveDescriptor()
    {
        // ClawFanCapability.TryValidateCurve admits six monotonic temperatures within 0..100.
        // This is a legal first-edit template, not a claim about the firmware's current table.
        return CapabilityDescriptors.Curve(CapabilityIds.FanCurve, new CapabilityDisplay { Key = DisplayKey.FanCurve },
            [0, 20, 40, 60, 80, 100], read: true);
    }

    private static CapabilityDescriptor LightingColorDescriptor(string instance, string label)
    {
        return CapabilityDescriptors.Color(CapabilityIds.LightingColor, instance, CapabilityDescriptors.Label(label),
            read: true, CapabilityPersistence.DevicePersistent);
    }

    private ValueTask PublishStatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishStates(null);
        return ValueTask.CompletedTask;
    }

    /// <summary>Publishes the states a service owns after its state changed outside a serialized turn.</summary>
    /// <param name="service">The service whose fault or reconnection callback changed its state.</param>
    /// <remarks>
    ///     A reader fault, a failed reconnection or a returned pad changes the controller's state on a transport
    ///     thread; publishing it there keeps the telemetry tick to the readings it samples.
    /// </remarks>
    private void PublishServiceStates(DeviceServiceStatus service)
    {
        PublishStates(service);
    }

    /// <summary>Publishes every state, or only those of one service.</summary>
    /// <remarks>
    ///     Controls carry only accepted writes and sensors their last reading, both from
    ///     <see cref="_observedValues" />; ownership is the owning service's state, timed when it last changed.
    ///     Locked so a state computed on a transport callback and one computed in a serialized turn are stored in
    ///     the order they were read.
    /// </remarks>
    private void PublishStates(DeviceServiceStatus? owner)
    {
        lock (_publishGate)
        {
            if (_descriptorSet is not { } descriptors)
            {
                return;
            }

            foreach (var descriptor in descriptors.Descriptors)
            {
                if (ServiceForCapability(descriptor.CapabilityId) is not { } service
                    || (owner is not null && service != owner))
                {
                    continue;
                }

                var owned = service.State is DeviceServiceState.Owned;
                var reason = owned ? null : service.Reason ?? DeviceServiceLifecycle.ReasonFor(service.State);
                _publications.Publish(Ownership(descriptor) is { } ownership
                    ? ProviderControls.State(descriptor.CapabilityId, owned, ownership, service.ChangedAt, reason,
                        descriptor.InstanceId)
                    : ProviderControls.Observed(descriptor, _observedValues, owned, reason));
            }
        }
    }

    /// <summary>Who holds the controller or the motion sensors; null for every other capability.</summary>
    private CapabilityValue? Ownership(CapabilityDescriptor descriptor)
    {
        return descriptor.CapabilityId switch
        {
            CapabilityIds.Controller => CapabilityValue.Choice(DeviceServiceLifecycle.Ownership(_controller?.State)),
            CapabilityIds.Motion => CapabilityValue.Choice(DeviceServiceLifecycle.Ownership(_motion?.State)),
            _ => null
        };
    }

    private static IReadOnlyList<OemControlDescriptor> CreateOemControls()
    {
        return
        [
            CapabilityDescriptors.OemControl("oem1", "Claw button", OemControlPlacement.Front,
                requiresController: false, CanonicalButtons.Guide),
            CapabilityDescriptors.OemControl("oem2", "Quick Settings", OemControlPlacement.Front,
                requiresController: false, CanonicalButtons.QuickAccess) with { SupportsLongPress = true },
            CapabilityDescriptors.OemControl("oem3", "M1", OemControlPlacement.Rear),
            CapabilityDescriptors.OemControl("oem4", "M2", OemControlPlacement.Rear)
        ];
    }
}
