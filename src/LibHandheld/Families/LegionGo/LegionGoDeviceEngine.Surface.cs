using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Families.LegionGo;

internal sealed partial class LegionGoDeviceEngine
{
    private const string LightingPower = "lighting.enabled";
    private const string GyroSide = "motion.gyro-side";
    private const string NativeTouchpad = "controller.touchpad-native";
    private const string ControllerMode = "controller.native-mode";
    private const string ControllerReset = "controller.reset-mappings";

    private void BuildSurface()
    {
        var model = _model!;
        List<CapabilityDescriptor> values =
        [
            Number(CapabilityIds.PowerSustained, "Sustained power limit", CapabilityRole.PowerSustainedLimit,
                    model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true) with
                {
                    PairedPowerLimitId = CapabilityIds.PowerBoost,
                    PowerPresets = Presets(model)
                },
            Number(CapabilityIds.PowerBoost, "Boost power limit", CapabilityRole.PowerSlowLimit,
                model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true),
            Choice(CapabilityIds.Scenario, "Performance mode", CapabilityRole.ScenarioMode,
                ["quiet", "balanced", "performance", "custom"], true, true),
            Choice(CapabilityIds.FanMode, "Fan mode", CapabilityRole.FanMode,
                ["automatic", "custom", "full-speed"], false, true),
            // WriteFanTable consumes the ten points at 10, 20, ... 100 degrees Celsius. EC-fan models
            // drive one target RPM and have no table, so they publish no curve.
            .. model.EcFan
                ? []
                : new[]
                {
                    CapabilityDescriptors.Curve(CapabilityIds.FanCurve, CapabilityDescriptors.Label("Fan curve"),
                        Enumerable.Range(1, 10).Select(index => index * 10).ToArray(), read: true)
                },
            CapabilityDescriptors.Boolean(CapabilityIds.ChargeLimit, CapabilityRole.ChargeProtectionMode,
                CapabilityDescriptors.Label("80% charge protection"), read: true,
                persistence: CapabilityPersistence.DevicePersistent),
            Number(CapabilityIds.FanRpm, "Fan speed", CapabilityRole.FanMeasuredRpm, 0, 20000,
                CapabilityUnit.Rpm, false),
            Number(CapabilityIds.Temperature, "CPU temperature", CapabilityRole.Telemetry, 0, 150,
                CapabilityUnit.Celsius, false),
            CapabilityDescriptors.Boolean(LightingPower, CapabilityRole.LightingPower,
                CapabilityDescriptors.Label("Lighting"), read: false,
                persistence: CapabilityPersistence.DevicePersistent),
            Number(CapabilityIds.LightingBrightness, "Brightness", CapabilityRole.LightingBrightness, 0, 100,
                    CapabilityUnit.Percent, true) with
                {
                    SupportsRead = false, Persistence = CapabilityPersistence.DevicePersistent
                },
            Number(CapabilityIds.LightingSpeed, "Effect speed", CapabilityRole.LightingEffectSpeed, 0, 100,
                    CapabilityUnit.Percent, true) with
                {
                    SupportsRead = false, Persistence = CapabilityPersistence.DevicePersistent
                },
            Choice(CapabilityIds.LightingEffect, "Lighting effect", CapabilityRole.LightingEffect,
                    ["solid", "breathing", "rainbow", "wheel"], false, true) with
                {
                    Persistence = CapabilityPersistence.DevicePersistent
                },
            Color("left", model.Slim ? "Stick rings" : "Left stick ring"),
            CapabilityDescriptors.Ownership(CapabilityIds.Controller, CapabilityRole.ControllerSource,
                CapabilityDescriptors.Label("Physical controller")),
            CapabilityDescriptors.Ownership(CapabilityIds.Motion, CapabilityRole.MotionSource,
                CapabilityDescriptors.Label("Motion sensors")),
            CapabilityDescriptors.Action(CapabilityIds.Rumble, CapabilityRole.HapticSink,
                CapabilityDescriptors.Label("Rumble")),
            CapabilityDescriptors.Boolean(NativeTouchpad, CapabilityRole.GenericToggle,
                CapabilityDescriptors.Label("Native mouse touchpad"), read: false),
            CapabilityDescriptors.Action(ControllerReset, CapabilityRole.GenericAction,
                CapabilityDescriptors.Label("Reset controller mappings"), CapabilityPersistence.DevicePersistent)
        ];
        values.Add(Number(CapabilityIds.FanDuty, "Fan duty", CapabilityRole.FanDuty, 0, 100, CapabilityUnit.Percent,
            true) with
        {
            SupportsRead = false
        });

        if (!model.Slim)
        {
            values.Add(Color("right", "Right stick ring"));
            values.Add(Choice(GyroSide, "Motion source controller", CapabilityRole.GenericChoice,
                ["left", "right"], false, true));
            values.Add(Choice(ControllerMode, "Native controller mode", CapabilityRole.GenericChoice,
                ["xinput", "dinput"], false, true));
        }

        _surface = new CapabilityDescriptorSet
        {
            Descriptors = values
        };
    }

    private static IReadOnlyList<DevicePowerPreset> Presets(LegionGoModel model)
    {
        var result = new List<DevicePowerPreset>();
        if (model.MinimumWatts <= 8)
        {
            result.Add(new DevicePowerPreset("quiet", "Quiet", 8, 8, DevicePowerMode.BetterBattery)
                { ScenarioOnAc = "quiet", ScenarioOnDc = "quiet" });
        }

        result.Add(new DevicePowerPreset("balanced", "Balanced", 15, 15, DevicePowerMode.Balanced)
            { ScenarioOnAc = "balanced", ScenarioOnDc = "balanced" });
        result.Add(new DevicePowerPreset("performance", "Performance", 25, 25, DevicePowerMode.BestPerformance)
            { ScenarioOnAc = "performance", ScenarioOnDc = "performance" });
        return result;
    }

    private async ValueTask PublishSurfaceAsync(CancellationToken token)
    {
        await _publications.PublishDescriptorsAsync(_surface!, token).ConfigureAwait(false);
        var names = _model!.Slim
            ? new[] { "Legion R", "Legion L", "Y1", "Y2" }
            :
            [
                "Legion R", "Legion L", "Y1", "Y2", "M3", "Y3", "M1", "M2", "Page", "Desktop", "Wheel click",
                "Wheel up", "Wheel down", "Touchpad secondary click"
            ];
        await _publications.PublishOemControlsAsync(names.Select((name, index) => new OemControlDescriptor
        {
            ControlId = LegionControls.Ids[index], Display = CapabilityDescriptors.Label(name),
            Placement = index < 2 || index >= 8 ? OemControlPlacement.Front : OemControlPlacement.Rear,
            RequiresControllerAcquisition = index >= 2, CompanionApplication = index == 1,
            DefaultActionHint = index == 13 ? OemControlDefaultActionHint.MouseSecondaryButton
                : index == 1 ? OemControlDefaultActionHint.CompanionApplication : OemControlDefaultActionHint.None
        }).ToArray(), token).ConfigureAwait(false);
        await RefreshObservedAsync(token).ConfigureAwait(false);
        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    private async ValueTask RefreshObservedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_wmi is not null)
        {
            Observe(CapabilityIds.Temperature, _wmi.ReadFeature(LegionProtocol.Temperature));
            Observe(CapabilityIds.FanRpm, _model!.EcFan ? ReadEcRpm() : _wmi.ReadFeature(LegionProtocol.FanRpm));
        }

        if (_hid is not null && _controllerWanted && !_quiescing && !_hid.Reading && !_hidFaulted)
        {
            await _hid.CloseAsync(token).ConfigureAwait(false);
            if (await _hid.OpenAsync(_model!, token).ConfigureAwait(false))
            {
                await AcquireOemAsync(token).ConfigureAwait(false);
                await AcquireControllerAsync(token).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PublishStatesAsync(CancellationToken token)
    {
        if (_surface is null)
        {
            return;
        }

        foreach (var descriptor in _surface.Descriptors)
        {
            var available = UsesHid(descriptor) ? _hid?.Connected == true : _wmi?.FeaturesAvailable == true;
            if (descriptor.CapabilityId == CapabilityIds.Scenario)
            {
                available = _wmi?.ModesAvailable == true;
            }

            if (IsFanControl(descriptor.CapabilityId))
            {
                available = FanTransportAvailable;
            }

            if (descriptor.CapabilityId is CapabilityIds.Controller or CapabilityIds.Motion)
            {
                available = true;
                var ownership =
                    CapabilityValue.Choice(_hid?.Reading == true ? SourceOwnership.Managed : SourceOwnership.Device);
                if (_observed.Value(descriptor.CapabilityId) != ownership)
                {
                    _observed.Read(descriptor.CapabilityId, ownership);
                }
            }

            if (descriptor.CapabilityId == CapabilityIds.Rumble)
            {
                available = _hid?.Reading == true && _hid.RumbleAvailable;
            }

            await _publications.PublishCapabilityStateAsync(ProviderControls.Observed(descriptor, _observed,
                available && !_quiescing,
                available
                    ? null
                    : new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                        "The native Lenovo transport for this control is unavailable.")), token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Whether the transport that drives the fan controls is open: the signed EC on EC-fan models, otherwise
    ///     the WMI feature and fan-table providers. Command admission and state publication both read it.
    /// </summary>
    private bool FanTransportAvailable => _model!.EcFan
        ? _ec is not null
        : _wmi?.FeaturesAvailable == true && _wmi.FansAvailable;

    private static bool IsFanControl(string id)
    {
        return id is CapabilityIds.FanCurve or CapabilityIds.FanMode or CapabilityIds.FanDuty;
    }

    /// <summary>Lighting and native controller controls use the HID command collection; the rest use WMI or the EC.</summary>
    private static bool UsesHid(CapabilityDescriptor descriptor)
    {
        return descriptor.Role is CapabilityRole.LightingBrightness or CapabilityRole.LightingEffect
                   or CapabilityRole.LightingEffectSpeed or CapabilityRole.LightingZoneColor
                   or CapabilityRole.LightingPower ||
               descriptor.CapabilityId is NativeTouchpad or GyroSide or ControllerMode or ControllerReset;
    }

    private void Observe(string id, int? value)
    {
        _observed.Read(id, value is { } integer ? CapabilityValue.Integer(integer) : null);
    }

    private int? ReadEcRpm()
    {
        if (_ec is null)
        {
            return null;
        }

        try
        {
            return _ec.ReadUInt16(0xC6C0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Change("legion-ec", "rpm-read", ex.Message, DeviceTraceLevel.Warn);
            return null;
        }
    }


    private static CapabilityDescriptor Number(string id, string label, CapabilityRole role, int minimum, int maximum,
        CapabilityUnit unit, bool writable)
    {
        return CapabilityDescriptors.Integer(id, role, CapabilityDescriptors.Label(label), minimum, maximum, unit,
            true, writable);
    }

    private static CapabilityDescriptor Choice(string id, string label, CapabilityRole role,
        IReadOnlyList<string> choices,
        bool read, bool write)
    {
        return CapabilityDescriptors.Choice(id, role, CapabilityDescriptors.Label(label),
            CapabilityDescriptors.Choices(choices), read, write);
    }

    private static CapabilityDescriptor Color(string side, string label)
    {
        return CapabilityDescriptors.Color(CapabilityIds.LightingColor, side, CapabilityDescriptors.Label(label), false,
            CapabilityPersistence.DevicePersistent);
    }

    private static string? ModeName(int value)
    {
        return value switch { 1 => "quiet", 2 => "balanced", 3 => "performance", 255 => "custom", _ => null };
    }

    private static int ModeValue(string value)
    {
        return value switch
        {
            "quiet" => 1, "balanced" => 2, "performance" => 3, "custom" => 255,
            _ => throw new ArgumentException("Invalid Lenovo mode.")
        };
    }

    private static int CurveAt(IReadOnlyList<CurvePoint> curve, int temperature)
    {
        if (temperature <= curve[0].Input)
        {
            return curve[0].Output;
        }

        for (var i = 1; i < curve.Count; i++)
        {
            if (temperature > curve[i].Input)
            {
                continue;
            }

            var first = curve[i - 1];
            var second = curve[i];
            return (int)Math.Round(first.Output + (second.Output - first.Output) * (temperature - first.Input) /
                (double)(second.Input - first.Input));
        }

        return curve[^1].Output;
    }
}
