using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

// The capability surface: descriptors, published states and OEM controls.
internal sealed partial class ClawDeviceEngine
{
    /// <summary>The Claw's declared Device overlay layout.</summary>
    /// <remarks>
    ///     Titles and icons are WSGM-owned vocabulary; only the grouping is this plugin's. A section a
    ///     unit leaves empty is dropped by WSGM rather than declared conditionally, so the layout stays
    ///     one static fact.
    /// </remarks>
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
            IntegerDescriptor(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    // PL1 shares PL2's ceiling: on the A2VM that is 37 W, not the 30 W it ships at.
                    DisplayKey.SustainedPowerLimit, Model.MinimumWatts, Model.MaximumWatts,
                    CapabilityUnit.Watt, true,
                    section: SectionIds.Power, category: CategoryIds.Limits, order: 0) with
                {
                    PowerPresets = Model.PowerPresets,
                    PairedPowerLimitId = CapabilityIds.PowerBoost
                },
            IntegerDescriptor(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                DisplayKey.BoostPowerLimit, Model.MinimumWatts, Model.MaximumWatts, CapabilityUnit.Watt, true,
                section: SectionIds.Power, category: CategoryIds.Limits, order: 1),
            IntegerDescriptor(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    ClawChargeLimitCapability.MinimumPercent,
                    ClawChargeLimitCapability.MaximumPercent,
                    CapabilityUnit.Percent,
                    true,
                    persistence: CapabilityPersistence.DevicePersistent,
                    section: SectionIds.Power,
                    category: CategoryIds.Charging) with
                {
                    // HC's BatteryBypassStep: the limit is 60, 80 or 100 percent.
                    Step = ClawChargeLimitCapability.StepPercent
                },
            new CapabilityDescriptor
            {
                CapabilityId = ClawChargeLimitCapability.ProtectionId,
                Role = CapabilityRole.ChargeProtectionMode,
                ValueKind = CapabilityValueKind.Boolean,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Battery Master" },
                SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            },
            ChoiceDescriptor(
                CapabilityIds.Scenario,
                CapabilityRole.ScenarioMode,
                DisplayKey.PerformanceProfile,
                ["comfort", "green", "eco", "user", "sport", "inactive"],
                true,
                SectionIds.Power),
            ChoiceDescriptor(
                CapabilityIds.FanMode,
                CapabilityRole.FanMode,
                DisplayKey.FanMode,
                ["automatic", "custom", "full-speed"],
                true,
                SectionIds.Power,
                CategoryIds.Control),
            FanCurveDescriptor(1),
            IntegerDescriptor("fan.duty", CapabilityRole.FanDuty, DisplayKey.FanSpeed, 0, 100,
                    CapabilityUnit.Percent, true, section: SectionIds.Power) with
                {
                    SupportsRead = false
                },
            IntegerDescriptor(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                DisplayKey.Brightness, 0, 100, CapabilityUnit.Percent, true,
                persistence: CapabilityPersistence.DevicePersistent,
                section: SectionIds.Lighting),
            LightingColorDescriptor(CapabilityInstances.LeftRing, "Left ring", 0),
            LightingColorDescriptor(CapabilityInstances.RightRing, "Right ring", 1),
            LightingColorDescriptor(CapabilityInstances.Buttons, "Buttons", 2),
            // These three carry the value kinds their roles require. ControllerSource and
            // MotionSource are choices because "who owns this source" has more than two answers:
            // the plugin can hold it, the device can still have it, or acquisition can have failed,
            // and a boolean flattened all three into "not owned". HapticSink carries no value at
            // all: it is a target rumble is written to, not something with a readable state.
            //
            // Declaring them as booleans made the descriptor set fail the SDK's role/value-kind
            // check, and a rejected SET means every capability is rejected, so the whole device
            // published nothing at all because of these three lines.
            ChoiceDescriptor(
                CapabilityIds.Controller,
                CapabilityRole.ControllerSource,
                DisplayKey.Controller,
                SourceOwnership.Choices,
                false,
                SectionIds.Info,
                CategoryIds.Ownership),
            ChoiceDescriptor(
                CapabilityIds.Motion,
                CapabilityRole.MotionSource,
                DisplayKey.Motion,
                SourceOwnership.Choices,
                false,
                SectionIds.Info,
                CategoryIds.Ownership,
                1),
            ActionDescriptor(
                CapabilityIds.Rumble,
                CapabilityRole.HapticSink,
                DisplayKey.Rumble,
                SectionIds.Info,
                CategoryIds.Ownership,
                2),
            // Readings, not controls. They left the Power page because a person opens it to change
            // how the device behaves, not to watch numbers; the CPU temperature stays published
            // because the fan-curve editor draws the live temperature against the curve.
            IntegerDescriptor(CapabilityIds.Temperature, CapabilityRole.Telemetry,
                DisplayKey.CpuTemperature, 0, 110, CapabilityUnit.Celsius, false,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 0),
            // Both fans are still measured separately even though they are driven together: one
            // failing fan is exactly the fault this page exists to make visible.
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanLeft, 0, 10_000, CapabilityUnit.Rpm, false,
                CapabilityInstances.Left,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 1),
            IntegerDescriptor(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanRight, 0, 10_000, CapabilityUnit.Rpm, false,
                CapabilityInstances.Right,
                section: SectionIds.Info, category: CategoryIds.Readings, order: 2)
        ];

        EnsureUniqueCapabilityKeys(descriptors);
        _descriptorSet = new CapabilityDescriptorSet
        {
            Descriptors = descriptors
        };
    }

    private static void EnsureUniqueCapabilityKeys(IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var key = CapabilityKey(
                descriptor.CapabilityId,
                descriptor.InstanceId);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException($"Capability '{key}' is registered more than once.");
            }
        }
    }

    private static string CapabilityKey(string capabilityId, string? instanceId)
    {
        return instanceId is null ? capabilityId : $"{capabilityId}/{instanceId}";
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
    private static CapabilityDescriptor FanCurveDescriptor(int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.FanCurve,
            Role = CapabilityRole.FanCurve,


            ValueKind = CapabilityValueKind.Curve,
            // ClawFanCapability.TryValidateCurve admits six monotonic temperatures within 0..100.
            // This is a legal first-edit template, not a claim about the firmware's current table.
            CurveInputs = [0, 20, 40, 60, 80, 100],
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100,
            Unit = CapabilityUnit.Percent,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor LightingColorDescriptor(
        string instance,
        string label,
        int order)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor,
            InstanceId = instance,
            Role = CapabilityRole.LightingZoneColor,


            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = label },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private async ValueTask PublishCapabilityStatesAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _descriptorSet is null)
        {
            return;
        }

        foreach (var descriptor in _descriptorSet.Descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = ServiceForCapability(descriptor.CapabilityId);
            if (service is null)
            {
                continue;
            }

            // Control intent comes only from accepted writes. Sensors carry actual readings; a haptic
            // sink carries availability alone because its declared value shape is None.
            var failed = _observationFailures.TryGetValue(service.ServiceId, out var observationFailure);
            var value = descriptor.ValueKind != CapabilityValueKind.None && !failed ? CurrentState(descriptor) : null;
            await _host.PublishCapabilityStateAsync(
                new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId,
                    InstanceId = descriptor.InstanceId,
                    Available = service.State is DeviceServiceState.Owned,
                    Reason = observationFailure ?? service.Reason ?? DeviceServiceLifecycle.ReasonFor(service.State),
                    ObservedValue = value,
                    Quality = value is null
                        ? HardwareStateQuality.Unknown
                        : HardwareStateQuality.Observed,
                    ObservedAt = descriptor.Role is CapabilityRole.ControllerSource or CapabilityRole.MotionSource
                        or CapabilityRole.HapticSink
                        ? service.ChangedAt
                        : value is null
                            ? null
                            : StateTimestamp(descriptor.CapabilityId)
                },
                cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private CapabilityValue? CurrentState(CapabilityDescriptor descriptor)
    {
        switch (descriptor.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
            {
                return _powerCapability!.WrittenLimits is { } value ? CapabilityValue.Integer(value.Sustained) : null;
            }
            case CapabilityIds.PowerBoost:
            {
                return _powerCapability!.WrittenLimits is { } value ? CapabilityValue.Integer(value.Boost) : null;
            }
            case CapabilityIds.Scenario:
            {
                return _powerCapability!.WrittenScenario is { } value ? Scenario(value, Model) : null;
            }
            case CapabilityIds.ChargeLimit:
            {
                // Publish only accepted intent, never the firmware's reset/unsupported charge register.
                return _chargeLimitCapability!.Written is { } value
                    ? CapabilityValue.Integer(value.Percent)
                    : null;
            }
            case ClawChargeLimitCapability.ProtectionId:
                return _chargeLimitCapability!.WrittenProtection is { } enabled
                    ? CapabilityValue.Boolean(enabled) : null;
            case CapabilityIds.FanMode:
            {
                return _fanCapability!.WrittenMode is { } value ? CapabilityValue.Choice(value) : null;
            }
            case "fan.duty":
                return _fanCapability?.WrittenDuty is { } duty ? CapabilityValue.Integer(duty) : null;
            case CapabilityIds.FanCurve:
            {
                // One accepted curve controls both channels; firmware tables are never polled for state.
                var value = _fanCapability!.WrittenCurve;
                return value is null ? null : CapabilityValue.Curve(value);
            }
            case CapabilityIds.FanRpm:
            {
                var value = _telemetry!.LastTelemetry;
                return value is null
                    ? null
                    : CapabilityValue.Integer(
                        descriptor.InstanceId == CapabilityInstances.Left ? value.LeftRpm : value.RightRpm);
            }
            case CapabilityIds.Temperature:
            {
                return _telemetry!.LastTelemetry is { } value
                    ? CapabilityValue.Integer(value.TemperatureCelsius)
                    : null;
            }
            case CapabilityIds.LightingBrightness:
            {
                return _lightingCapability!.Current is { } value ? CapabilityValue.Integer(value.Brightness) : null;
            }
            case CapabilityIds.LightingColor:
            {
                var value = _lightingCapability!.Current;
                return value is null
                    ? null
                    : CapabilityValue.Color(descriptor.InstanceId switch
                    {
                        CapabilityInstances.RightRing => value.RightRingColor,
                        CapabilityInstances.LeftRing => value.LeftRingColor,
                        CapabilityInstances.Buttons => value.ButtonsColor,
                        _ => 0
                    });
            }
            case CapabilityIds.Rumble:
                // A sink has no value to report. Its descriptor says so, and its state has to agree or
                // the state is rejected for a kind mismatch the way the descriptor set was.
                return CapabilityValue.None();
        }

        return CapabilityValue.Choice(DeviceServiceLifecycle.Ownership(
            descriptor.CapabilityId == CapabilityIds.Motion ? _motion!.State : _controller!.State));
    }

    private DateTimeOffset? StateTimestamp(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost => _powerCapability!.LimitsWrittenAt,
            CapabilityIds.Scenario => _powerCapability!.ScenarioWrittenAt,
            CapabilityIds.ChargeLimit => _chargeLimitCapability!.WrittenAt,
            ClawChargeLimitCapability.ProtectionId => _chargeLimitCapability!.ProtectionWrittenAt,
            CapabilityIds.FanMode => _fanCapability!.ModeWrittenAt,
            "fan.duty" => _fanCapability!.DutyWrittenAt,
            CapabilityIds.FanCurve => _fanCapability!.CurveWrittenAt,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lightingCapability!.WrittenAt,
            CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry!.ObservedAt,
            _ => null
        };
    }

    private static IReadOnlyList<OemControlDescriptor> CreateOemControls()
    {
        return
        [
            Oem("oem1", "Claw button", OemControlPlacement.Front, false,
                false),
            Oem("oem2", "Quick Settings", OemControlPlacement.Front, true,
                false),
            Oem("oem3", "M1", OemControlPlacement.Rear, false,
                true),
            Oem("oem4", "M2", OemControlPlacement.Rear, false,
                true)
        ];
    }

    private static OemControlDescriptor Oem(
        string id,
        string label,
        OemControlPlacement placement,
        bool supportsLongPress,
        bool requiresController)
    {
        return new OemControlDescriptor
        {
            ControlId = id,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = label },
            Placement = placement,
            SupportsLongPress = supportsLongPress,
            RequiresControllerAcquisition = requiresController
        };
    }

    private static CapabilityDescriptor IntegerDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        CapabilityUnit unit,
        bool writable,
        string? instance = null,
        CapabilityPersistence persistence = CapabilityPersistence.Volatile,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            InstanceId = instance,
            Role = role,


            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = unit,
            Persistence = persistence
        };
    }

    private static CapabilityDescriptor ChoiceDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        IReadOnlyList<string> choices,
        bool writable,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,


            ValueKind = CapabilityValueKind.Choice,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = writable,
            Choices =
            [
                .. choices.Select(choice => new CapabilityChoice(
                    choice,
                    new CapabilityDisplay
                    {
                        Key = DisplayKey.Custom, CustomText = choice switch
                        {
                            "comfort" => "Comfort", "green" => "Green", "eco" => "Eco", "sport" => "Sport",
                            "user" => "User", "inactive" => "Inactive", "automatic" => "Automatic",
                            "custom" => "Custom", "full-speed" => "Full speed",
                            _ => choice
                        }
                    }))
            ],
            Persistence = CapabilityPersistence.Volatile
        };
    }

    /// <summary>A capability that is invoked rather than read or written.</summary>
    /// <param name="id">Capability id.</param>
    /// <param name="role">Semantic role, which must be one the SDK maps to <c>None</c>.</param>
    /// <param name="display">Display key.</param>
    /// <param name="section">Overlay section id the row is grouped under, or null for the default.</param>
    /// <param name="category">Category within that section, or null for its lead group.</param>
    /// <param name="order">Sort order within the section; lower sorts first.</param>
    /// <returns>The descriptor.</returns>
    private static CapabilityDescriptor ActionDescriptor(
        string id,
        CapabilityRole role,
        DisplayKey display,
        string? section = null,
        string? category = null,
        int order = 0)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,


            ValueKind = CapabilityValueKind.None,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = false,
            SupportsWrite = false,
            // A descriptor has to offer at least one operation, and for a sink the operation is the
            // invoke: rumble is written to it, never read back from it.
            SupportsAction = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    internal static CapabilityValue? Scenario(byte raw, ClawModel model)
    {
        var mode = raw & 0x3F;
        var choice =
            (raw & 0xC0) != 0xC0
                ? "inactive"
                : mode == model.UserScenario
                    ? "user"
                    : mode switch
                    {
                        0 => "comfort",
                        1 => "green",
                        2 => "eco",
                        4 => "sport",
                        _ => null
                    };
        return choice is null ? null : CapabilityValue.Choice(choice);
    }
}
