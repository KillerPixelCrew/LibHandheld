using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Families.LegionGo;

internal sealed partial class LegionGoDeviceEngine
{
    private const string LightingPower = "lighting.enabled";
    private const string FanDuty = "fan.duty";
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
                    model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, DeviceSections.PowerId) with
                {
                    PairedPowerLimitId = CapabilityIds.PowerBoost,
                    PowerPresets = Presets(model)
                },
            Number(CapabilityIds.PowerBoost, "Boost power limit", CapabilityRole.PowerSlowLimit,
                model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, DeviceSections.PowerId),
            Choice(CapabilityIds.Scenario, "Performance mode", CapabilityRole.ScenarioMode,
                ["quiet", "balanced", "performance", "custom"], true, true, DeviceSections.PowerId),
            Choice(CapabilityIds.FanMode, "Fan mode", CapabilityRole.FanMode,
                ["automatic", "custom", "full-speed"], false, true, DeviceSections.PowerId),
            new()
            {
                CapabilityId = CapabilityIds.FanCurve, Role = CapabilityRole.FanCurve,
                ValueKind = CapabilityValueKind.Curve, Display = CapabilityDescriptors.Label("Fan curve"),
                Minimum = 0, Maximum = 100, Unit = CapabilityUnit.Percent,
                SupportsRead = !model.EcFan, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
            },
            new()
            {
                CapabilityId = CapabilityIds.ChargeLimit, Role = CapabilityRole.ChargeProtectionMode,
                ValueKind = CapabilityValueKind.Boolean, Display = CapabilityDescriptors.Label("80% charge protection"),
                SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
            },
            Number(CapabilityIds.FanRpm, "Fan speed", CapabilityRole.FanMeasuredRpm, 0, 20000,
                CapabilityUnit.Rpm, false, DeviceSections.InfoId),
            Number(CapabilityIds.Temperature, "CPU temperature", CapabilityRole.Telemetry, 0, 150,
                CapabilityUnit.Celsius, false, DeviceSections.InfoId),
            new()
            {
                CapabilityId = LightingPower, Role = CapabilityRole.LightingPower,
                ValueKind = CapabilityValueKind.Boolean,
                Display = CapabilityDescriptors.Label("Lighting"), SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            },
            Number(CapabilityIds.LightingBrightness, "Brightness", CapabilityRole.LightingBrightness, 0, 100,
                    CapabilityUnit.Percent, true, DeviceSections.RgbId) with
                {
                    SupportsRead = false, Persistence = CapabilityPersistence.DevicePersistent
                },
            Number(CapabilityIds.LightingSpeed, "Effect speed", CapabilityRole.LightingEffectSpeed, 0, 100,
                    CapabilityUnit.Percent, true, DeviceSections.RgbId) with
                {
                    SupportsRead = false, Persistence = CapabilityPersistence.DevicePersistent
                },
            Choice(CapabilityIds.LightingEffect, "Lighting effect", CapabilityRole.LightingEffect,
                    ["solid", "breathing", "rainbow", "wheel"], false, true, DeviceSections.RgbId) with
                {
                    Persistence = CapabilityPersistence.DevicePersistent
                },
            Color("left", model.Slim ? "Stick rings" : "Left stick ring"),
            Choice(CapabilityIds.Controller, "Physical controller", CapabilityRole.ControllerSource,
                SourceOwnership.Choices,
                true, false, DeviceSections.InfoId),
            Choice(CapabilityIds.Motion, "Motion sensors", CapabilityRole.MotionSource, SourceOwnership.Choices,
                true, false, DeviceSections.InfoId),
            new()
            {
                CapabilityId = CapabilityIds.Rumble, Role = CapabilityRole.HapticSink,
                ValueKind = CapabilityValueKind.None,
                Display = CapabilityDescriptors.Label("Rumble"), SupportsAction = true,
                Persistence = CapabilityPersistence.Volatile
            },
            new()
            {
                CapabilityId = NativeTouchpad, Role = CapabilityRole.GenericToggle,
                ValueKind = CapabilityValueKind.Boolean,
                Display = CapabilityDescriptors.Label("Native mouse touchpad"), SupportsWrite = true,
                Persistence = CapabilityPersistence.Volatile
            },
            new()
            {
                CapabilityId = ControllerReset, Role = CapabilityRole.GenericAction,
                ValueKind = CapabilityValueKind.None,
                Display = CapabilityDescriptors.Label("Reset controller mappings"), SupportsAction = true,
                Persistence = CapabilityPersistence.DevicePersistent
            }
        ];
        if (model.EcFan)
        {
            values.RemoveAll(descriptor => descriptor.Role == CapabilityRole.FanCurve);
        }

        {
            values.Add(Number(FanDuty, "Fan duty", CapabilityRole.FanDuty, 0, 100, CapabilityUnit.Percent, true,
                    DeviceSections.PowerId) with
                {
                    SupportsRead = false
                });
        }

        if (!model.Slim)
        {
            values.Add(Color("right", "Right stick ring"));
            values.Add(Choice(GyroSide, "Motion source controller", CapabilityRole.GenericChoice,
                ["left", "right"], false, true, DeviceSections.ControllerId));
            values.Add(Choice(ControllerMode, "Native controller mode", CapabilityRole.GenericChoice,
                ["xinput", "dinput"], false, true, DeviceSections.ControllerId));
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
        await _host!.PublishDescriptorsAsync(_surface!, token).ConfigureAwait(false);
        var names = _model!.Slim
            ? new[] { "Legion R", "Legion L", "Y1", "Y2" }
            :
            [
                "Legion R", "Legion L", "Y1", "Y2", "M3", "Y3", "M1", "M2", "Page", "Desktop", "Wheel click",
                "Wheel up", "Wheel down", "Touchpad secondary click"
            ];
        await _host.PublishOemControlsAsync(names.Select((name, index) => new OemControlDescriptor
        {
            ControlId = LegionControls.Ids[index], Display = CapabilityDescriptors.Label(name),
            Placement = index < 2 || index >= 8 ? OemControlPlacement.Front : OemControlPlacement.Rear,
            RequiresControllerAcquisition = index >= 2, CompanionApplication = index == 1,
            DefaultActionHint = index == 13 ? OemControlDefaultActionHint.MouseSecondaryButton
                : index == 1 ? OemControlDefaultActionHint.CompanionApplication : OemControlDefaultActionHint.None
        }).ToArray(), token).ConfigureAwait(false);
        await RefreshAsync(null, token).ConfigureAwait(false);
        await PublishStatesAsync(token).ConfigureAwait(false);
    }

    private async ValueTask RefreshAsync(string? id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_wmi is not null)
        {
            Observe(CapabilityIds.PowerSustained, _wmi.ReadFeature(LegionProtocol.LongPower));
            Observe(CapabilityIds.PowerBoost, _wmi.ReadFeature(LegionProtocol.ShortPower));
            Observe(CapabilityIds.Temperature, _wmi.ReadFeature(LegionProtocol.Temperature));
            Observe(CapabilityIds.FanRpm, _model!.EcFan ? ReadEcRpm() : _wmi.ReadFeature(LegionProtocol.FanRpm));
            var name = _wmi.ReadMode() is { } mode ? ModeName(mode) : null;
            _observed.Read(CapabilityIds.Scenario, name is null ? null : CapabilityValue.Choice(name));
            var charge = _wmi.ReadFeature(LegionProtocol.ChargeProtection);
            _observed.Read(CapabilityIds.ChargeLimit, charge is null ? null : CapabilityValue.Boolean(charge == 1));

            if (!_model.EcFan && _wmi.ReadFanTable() is { } table)
            {
                _observed.Read(CapabilityIds.FanCurve,
                    CapabilityValue.Curve(table.Select((v, i) => new CurvePoint((i + 1) * 10, v)).ToArray()));
            }
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
        if (_surface is null || _host is null)
        {
            return;
        }

        foreach (var descriptor in _surface.Descriptors)
        {
            var key = (descriptor.CapabilityId, descriptor.InstanceId);
            var available = descriptor.Role is CapabilityRole.LightingBrightness or CapabilityRole.LightingEffect
                                or CapabilityRole.LightingEffectSpeed or CapabilityRole.LightingZoneColor
                                or CapabilityRole.LightingPower ||
                            descriptor.CapabilityId is NativeTouchpad or GyroSide or ControllerMode or ControllerReset
                ? _hid?.Connected == true
                : _wmi?.FeaturesAvailable == true;
            if (descriptor.CapabilityId == CapabilityIds.Scenario)
            {
                available = _wmi?.ModesAvailable == true;
            }

            if (!_model!.EcFan && descriptor.CapabilityId is CapabilityIds.FanCurve or CapabilityIds.FanMode or FanDuty)
            {
                available &= _wmi?.FansAvailable == true;
            }

            if (_model!.EcFan && descriptor.CapabilityId is FanDuty or CapabilityIds.FanCurve or CapabilityIds.FanMode)
            {
                available = _ec is not null && !_ecFaulted;
            }

            if (descriptor.CapabilityId is CapabilityIds.Controller or CapabilityIds.Motion)
            {
                available = true;
                var ownership =
                    CapabilityValue.Choice(_hid?.Reading == true ? SourceOwnership.Managed : SourceOwnership.Device);
                if (_observed.Value(key.CapabilityId) != ownership)
                {
                    _observed.Read(key.CapabilityId, ownership);
                }
            }

            if (descriptor.CapabilityId == CapabilityIds.Rumble)
            {
                available = _hid?.Reading == true && _hid.RumbleAvailable;
            }

            var observed = _observed.Value(key.CapabilityId, key.InstanceId);
            await _host.PublishCapabilityStateAsync(new CapabilityState
            {
                CapabilityId = key.CapabilityId, InstanceId = key.InstanceId, Available = available && !_quiescing,
                ObservedValue = observed,
                Quality = observed is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = _observed.Time(key.CapabilityId, key.InstanceId),


                Reason = available
                    ? null
                    : new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                        "The native Lenovo transport for this control is unavailable.")
            }, token).ConfigureAwait(false);
        }
    }

    private void Observe(string id, int? value)
    {
        _observed.Read(id, value is { } integer ? CapabilityValue.Integer(integer) : null);
    }

    private int? ReadEcRpm()
    {
        if (_ec is null || _ecFaulted)
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
        CapabilityUnit unit, bool writable, string section)
    {
        return CapabilityDescriptors.Integer(id, role, CapabilityDescriptors.Label(label), minimum, maximum, unit,
            true, writable);
    }

    private static CapabilityDescriptor Choice(string id, string label, CapabilityRole role,
        IReadOnlyList<string> choices,
        bool read, bool write, string section)
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
