using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Services;

namespace LibHandheld.Internal.Devices;

internal sealed partial class ProfiledDeviceEngine
{
    private const string FanDutyId = "fan.duty";

    private void BuildSurface()
    {
        List<CapabilityDescriptor> descriptors =
        [
            Range(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit, "Sustained power limit",
                    _profile.MinimumWatts, _profile.MaximumWatts, CapabilityUnit.Watt, true) with
                {
                    PairedPowerLimitId = CapabilityIds.PowerBoost
                },
            Range(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit, "Boost power limit",
                _profile.MinimumWatts, _profile.MaximumWatts, CapabilityUnit.Watt, true)
        ];
        if (_profile.Fan is { } fan)
        {
            descriptors.Add(new CapabilityDescriptor
            {
                CapabilityId = CapabilityIds.FanMode, Role = CapabilityRole.FanMode,
                ValueKind = CapabilityValueKind.Choice, Display = Label("Fan mode"), SupportsWrite = true,
                SupportsRead = fan.ManualAddress is not null, Persistence = CapabilityPersistence.Volatile,
                Choices = Array.AsReadOnly(new[] { "automatic", "custom", "full-speed" }
                    .Select(value => new CapabilityChoice(value, Label(value switch
                    {
                        "automatic" => "Automatic", "custom" => "Custom", _ => "Full speed"
                    }))).ToArray())
            });
            descriptors.Add(
                Range(FanDutyId, CapabilityRole.FanDuty, "Fan speed", 0, 100, CapabilityUnit.Percent, true) with
                {
                    SupportsRead = fan.ReadDuty
                });
            if (fan.RpmHighAddress is not null)
            {
                descriptors.Add(Range(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm, "Fan RPM", 0, 65535,
                    CapabilityUnit.Rpm, false));
            }
        }

        if (_profile.Controller is not null)
        {
            descriptors.Add(Ownership(CapabilityIds.Controller, CapabilityRole.ControllerSource, "Controller source"));
            descriptors.Add(new CapabilityDescriptor
            {
                CapabilityId = CapabilityIds.Rumble, Role = CapabilityRole.HapticSink,
                ValueKind = CapabilityValueKind.None, Display = Label("Rumble"),
                SupportsAction = true, Persistence = CapabilityPersistence.Volatile
            });
        }

        if (_profile.Motion is not null)
        {
            descriptors.Add(Ownership(CapabilityIds.Motion, CapabilityRole.MotionSource, "Motion sensors"));
        }

        if (_native is not null)
        {
            descriptors.AddRange(_native.Descriptors);
        }

        if (descriptors.GroupBy(item => (item.CapabilityId, item.InstanceId)).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("A native family service declared a duplicate capability identity.");
        }

        _surface = new CapabilityDescriptorSet
        {
            Generation = ++_generation, CycleGeneration = _cycle,
            Descriptors = Array.AsReadOnly(descriptors.ToArray())
        };
        _nativeDescriptorGeneration = _native?.DescriptorGeneration ?? 0;
    }

    private async ValueTask PublishAsync(CancellationToken token)
    {
        if (_sink is null || _surface is null)
        {
            return;
        }

        foreach (var descriptor in _surface.Descriptors)
        {
            if (_native?.States.SingleOrDefault(state =>
                    state.CapabilityId == descriptor.CapabilityId && state.InstanceId == descriptor.InstanceId) is
                { } nativeState)
            {
                await _sink.PublishCapabilityStateAsync(
                        nativeState with { DescriptorGeneration = _surface.Generation, CycleGeneration = _cycle },
                        token)
                    .ConfigureAwait(false);
                continue;
            }

            var service = descriptor.Role switch
            {
                CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit => _power,
                CapabilityRole.FanMode or CapabilityRole.FanDuty or CapabilityRole.FanMeasuredRpm =>
                    (DeviceServiceStatus?)_fan,
                CapabilityRole.ControllerSource or CapabilityRole.HapticSink => _controller,
                CapabilityRole.MotionSource => _services.SingleOrDefault(service => service.ServiceId == "motion"),
                _ => null
            };
            var available = service?.State == DeviceServiceState.Owned;
            if (descriptor.Role is CapabilityRole.ControllerSource or CapabilityRole.HapticSink)
            {
                available = _manageController && _controller?.Connected == true;
            }

            if (descriptor.Role == CapabilityRole.HapticSink)
            {
                available &= _controller?.SupportsRumble == true;
            }

            var value = descriptor.Role switch
            {
                CapabilityRole.PowerSustainedLimit => _power?.Observed is { } limits
                    ? CapabilityValue.Integer((int)Math.Round(limits.SustainedWatts))
                    : null,
                CapabilityRole.PowerSlowLimit => _power?.Observed is { } limits
                    ? CapabilityValue.Integer((int)Math.Round(limits.SlowWatts))
                    : null,
                CapabilityRole.FanMode => _fan?.Mode is { } mode ? CapabilityValue.Choice(mode) : null,
                CapabilityRole.FanDuty => _fan?.Duty is { } duty
                    ? CapabilityValue.Integer(Math.Clamp(duty, 0, 100))
                    : null,
                CapabilityRole.FanMeasuredRpm => _fan?.Rpm is { } rpm ? CapabilityValue.Integer(rpm) : null,
                CapabilityRole.ControllerSource => CapabilityValue.Choice(available
                    ? SourceOwnership.Plugin
                    : SourceOwnership.Device),
                CapabilityRole.MotionSource => CapabilityValue.Choice(available
                    ? SourceOwnership.Plugin
                    : SourceOwnership.Unavailable),
                _ => null
            };
            await _sink.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
                Available = available, Reason = available ? null : service?.Reason,
                ObservedValue = value,
                Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = value is null
                    ? null
                    : descriptor.Role switch
                    {
                        CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit => _power?.ObservedAt,
                        CapabilityRole.FanMode or CapabilityRole.FanDuty or CapabilityRole.FanMeasuredRpm => _fan
                            ?.ObservedAt,
                        _ => DateTimeOffset.UtcNow
                    },
                DescriptorGeneration = _surface.Generation, CycleGeneration = _cycle
            }, token).ConfigureAwait(false);
        }
    }

    private static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = text };
    }

    private static CapabilityDescriptor Range(string id, CapabilityRole role, string label, int minimum, int maximum,
        CapabilityUnit unit, bool write)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Integer, Display = Label(label),
            Minimum = minimum, Maximum = maximum, Step = 1, Unit = unit, SupportsRead = true,
            SupportsWrite = write, Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Ownership(string id, CapabilityRole role, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Choice, Display = Label(label),
            SupportsRead = true, Persistence = CapabilityPersistence.Volatile,
            Choices = Array.AsReadOnly(SourceOwnership.Choices
                .Select(value => new CapabilityChoice(value, Label(value))).ToArray())
        };
    }
}
