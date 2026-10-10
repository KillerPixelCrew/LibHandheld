using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.LegionGo;

internal sealed partial class LegionGoDeviceEngine
{
    private async ValueTask<CapabilityCommandResult> ExecuteCoreAsync(CapabilityCommand command,
        CancellationToken token)
    {
        var surface = _surface!;


        if (command.Deadline.HasExpired || token.IsCancellationRequested ||
            !DeviceWriteBudget.IsAvailable(command.Deadline))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "No native write was admitted after cancellation or deadline.");
        }

        var identity = _hardware.Identity();
        if (LegionGoModels.Match(identity) != _model ||
            LegionIdentity.Binding(identity) != LegionIdentity.Binding(_identity!))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "The Lenovo machine or BIOS changed.");
        }

        var descriptor = surface.Descriptors.SingleOrDefault(d =>
            d.CapabilityId == command.CapabilityId && d.InstanceId == command.InstanceId);
        var value = command.RequestedValue;
        var action = descriptor?.SupportsAction == true && descriptor.ValueKind == CapabilityValueKind.None;
        if (descriptor is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "No native Lenovo control has this identity.");
        }

        if (CommandAdmission.Validate(command, descriptor, surface.Descriptors) is { } invalid)
        {
            return CommandResults.Rejected(command, invalid);
        }

        value ??= CapabilityValue.None();

        if (descriptor.Role is CapabilityRole.LightingBrightness or CapabilityRole.LightingEffect
                or CapabilityRole.LightingEffectSpeed or CapabilityRole.LightingZoneColor
                or CapabilityRole.LightingPower ||
            command.CapabilityId is NativeTouchpad or GyroSide or ControllerMode or ControllerReset)
        {
            if (_hid?.Connected != true)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                    "The Lenovo HID transport is unavailable.");
            }
        }
        else if (_wmi is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The Lenovo WMI transport is unavailable.");
        }

        if (command.CapabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
                or CapabilityIds.Scenario && _wmi?.ModesAvailable != true)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The Lenovo mode provider is unavailable.");
        }

        if (command.CapabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
                or CapabilityIds.ChargeLimit && _wmi?.FeaturesAvailable != true)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The Lenovo feature provider is unavailable.");
        }

        if (!_model!.EcFan && command.CapabilityId is CapabilityIds.FanCurve or CapabilityIds.FanMode &&
            _wmi?.FansAvailable != true)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The Lenovo fan table provider is unavailable.");
        }

        if (_model!.EcFan && command.CapabilityId is FanDuty or CapabilityIds.FanCurve or CapabilityIds.FanMode &&
            _ec is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The signed EC transport is unavailable.");
        }

        var dispatched = false;
        try
        {
            using var budget = command.Deadline.CreateCancellationSource(token);
            var writeToken = budget.Token;
            switch (command.CapabilityId)
            {
                case ControllerReset:
                    await _hid!.StopAsync(writeToken).ConfigureAwait(false);
                    dispatched = true;
                    foreach (var report in LegionProtocol.ResetMappings(_model!))
                    {
                        await _hid.WriteAsync(report, writeToken).ConfigureAwait(false);
                    }

                    if (_controllerWanted)
                    {
                        await AcquireControllerAsync(writeToken).ConfigureAwait(false);
                    }

                    break;
                case CapabilityIds.PowerSustained:
                case CapabilityIds.PowerBoost:
                    if (!DevicePowerPair.TryResolve(command, surface.Descriptors, out var sustained, out var boost,
                            out var error))
                    {
                        return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                    }

                    await CapturePowerAsync("power", writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    _wmi!.WriteMode(255);
                    _wmi.WriteFeature(LegionProtocol.LongPower, sustained);
                    _wmi.WriteFeature(LegionProtocol.CrossPower, sustained);
                    _wmi.WriteFeature(LegionProtocol.ShortPower, boost);
                    _wmi.WriteFeature(LegionProtocol.PeakPower, boost);
                    Remember(CapabilityIds.PowerSustained, CapabilityValue.Integer(sustained));
                    Remember(CapabilityIds.PowerBoost, CapabilityValue.Integer(boost));
                    Remember(CapabilityIds.Scenario, CapabilityValue.Choice("custom"));
                    break;
                case CapabilityIds.Scenario:
                    await CapturePowerAsync("power", writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    _wmi!.WriteMode(ModeValue(value.ChoiceValue!));
                    _observed.Forget(CapabilityIds.PowerSustained);
                    _observed.Forget(CapabilityIds.PowerBoost);
                    break;
                case CapabilityIds.ChargeLimit:
                    dispatched = true;
                    _wmi!.WriteFeature(LegionProtocol.ChargeProtection, value.BooleanValue == true ? 1 : 0);
                    break;
                case CapabilityIds.FanCurve:
                    await CaptureFansAsync(writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    _fansChanged = true;
                    var table = Enumerable.Range(1, 10).Select(i => (ushort)CurveAt(value.CurveValue, i * 10))
                        .ToArray();
                    _wmi!.WriteFanTable(table);
                    _fanMode = "custom";

                    Remember(CapabilityIds.FanMode, CapabilityValue.Choice(_fanMode));
                    break;
                case FanDuty:
                    await CaptureFansAsync(writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    _fansChanged = true;
                    var targetRpm = LegionProtocol.FanPercentToRpm(value.IntegerValue!.Value);
                    if (_model.EcFan)
                    {
                        _ec!.WriteUInt16(0xC6C8, (ushort)targetRpm);
                    }
                    else
                    {
                        _wmi!.WriteFanTable(Enumerable.Repeat(checked((ushort)value.IntegerValue.Value), 10).ToArray());
                    }

                    _fanMode = "custom";
                    Remember(CapabilityIds.FanMode, CapabilityValue.Choice(_fanMode));
                    break;
                case CapabilityIds.FanMode:
                    await CaptureFansAsync(writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    _fansChanged = true;
                    _wmi!.WriteFeature(LegionProtocol.FullFan, value.ChoiceValue == "full-speed" ? 1 : 0);
                    if (value.ChoiceValue == "automatic")
                    {
                        if (_model.EcFan)
                        {
                            _ec!.WriteUInt16(0xC6C8, 0);
                        }
                        else if (_journal!.OriginalStateFor<LegionRecoveryState>("fans")?.FanTable is { } original)
                        {
                            _wmi.WriteFanTable(original);
                        }
                        else
                        {
                            _wmi.WriteFanTable([44, 48, 55, 60, 71, 79, 87, 87, 100, 100]);
                        }
                    }

                    _fanMode = value.ChoiceValue!;
                    if (_fanMode == "automatic")
                    {
                        _observed.Forget(FanDuty);
                        _observed.Forget(CapabilityIds.FanCurve);
                    }

                    break;
                case LightingPower:
                    dispatched = true;
                    foreach (var report in LegionProtocol.LightingEnabled(_model, value.BooleanValue!.Value))
                    {
                        await _hid!.WriteAsync(report, writeToken).ConfigureAwait(false);
                    }

                    _lightingEnabled = value.BooleanValue.Value;
                    break;
                case CapabilityIds.LightingBrightness:
                case CapabilityIds.LightingSpeed:
                case CapabilityIds.LightingEffect:
                case CapabilityIds.LightingColor:
                    var brightness = _brightness;
                    var speed = _speed;
                    var effect = _effect;
                    var leftColor = _leftColor;
                    var rightColor = _rightColor;
                    if (command.CapabilityId == CapabilityIds.LightingBrightness)
                    {
                        brightness = value.IntegerValue!.Value;
                    }
                    else if (command.CapabilityId == CapabilityIds.LightingSpeed)
                    {
                        speed = value.IntegerValue!.Value;
                    }
                    else if (command.CapabilityId == CapabilityIds.LightingEffect)
                    {
                        effect = value.ChoiceValue!;
                    }
                    else if (command.InstanceId == "right")
                    {
                        rightColor = value.ColorValue!.Value;
                    }
                    else
                    {
                        leftColor = value.ColorValue!.Value;
                    }

                    dispatched = true;
                    await _hid!.WriteAsync(LegionProtocol.Rgb(_model, 3, effect, leftColor, brightness, speed),
                        writeToken).ConfigureAwait(false);
                    if (_model.Slim)
                    {
                        await _hid.WriteAsync(LegionProtocol.SlimReport([0x10, 2, 3]), writeToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _hid.WriteAsync(LegionProtocol.Rgb(_model, 4, effect, rightColor, brightness, speed),
                            writeToken).ConfigureAwait(false);
                        await _hid.WriteAsync([5, 6, 0x73, 2, 3, 3, 1], writeToken).ConfigureAwait(false);
                        await _hid.WriteAsync([5, 6, 0x73, 2, 4, 3, 1], writeToken).ConfigureAwait(false);
                    }

                    foreach (var report in LegionProtocol.LightingEnabled(_model, _lightingEnabled))
                    {
                        await _hid.WriteAsync(report, writeToken).ConfigureAwait(false);
                    }

                    _brightness = brightness;
                    _speed = speed;
                    _effect = effect;
                    _leftColor = leftColor;
                    _rightColor = rightColor;

                    break;
                case NativeTouchpad:
                    dispatched = true;
                    foreach (var report in LegionProtocol.Touchpad(_model, value.BooleanValue!.Value))
                    {
                        await _hid!.WriteAsync(report, writeToken).ConfigureAwait(false);
                    }

                    _hid!.NativeTouchpad = value.BooleanValue.Value;
                    break;
                case GyroSide:
                    _hid!.RightGyro = value.ChoiceValue == "right";
                    break;
                case ControllerMode:
                    dispatched = true;
                    await _hid!.WriteAsync(LegionProtocol.ControllerMode(value.ChoiceValue == "dinput"), writeToken)
                        .ConfigureAwait(false);
                    // Re-enumeration is expected; observation binds the newly arrived approved collection.
                    await _hid.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    BeginReconnect();
                    break;
                default:
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "No native Lenovo command exists for this descriptor.");
            }

            if (!action)
            {
                Remember(command.CapabilityId, value, command.InstanceId);
            }

            // Acknowledged values remain authoritative; observation refreshes read-only sensors.
            return CommandResults.Applied(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, ex);
        }
    }

    private void Remember(string id, CapabilityValue value, string? instance = null)
    {
        _observed.Written(id, value, instance);
    }

    private async ValueTask CapturePowerAsync(string service, CancellationToken token)
    {
        await _journal!.BeginAsync(service, LegionIdentity.Binding(_identity!), () =>
        {
            Dictionary<int, int> values = [];
            foreach (var id in new[]
                     {
                         LegionProtocol.LongPower, LegionProtocol.CrossPower, LegionProtocol.ShortPower,
                         LegionProtocol.PeakPower
                     })
            {
                if (_wmi!.ReadFeature(id) is { } value)
                {
                    values[id] = value;
                }
            }

            var observedMode = _wmi!.ReadMode();
            var mode = observedMode is { } known && ModeName(known) is not null ? observedMode : null;
            return new LegionRecoveryState
                { Features = values, Mode = mode, UnknownOriginal = values.Count != 4 || mode is null };
        }, token).ConfigureAwait(false);
    }

    private async ValueTask CaptureFansAsync(CancellationToken token)
    {
        await _journal!.BeginAsync("fans", LegionIdentity.Binding(_identity!), () =>
        {
            var full = _wmi!.ReadFeature(LegionProtocol.FullFan);
            var table = _model!.EcFan ? null : _wmi.ReadFanTable();
            ushort? rpm = _model.EcFan && _ec is not null ? _ec.ReadUInt16(0xC6C8) : null;
            return new LegionRecoveryState
            {
                Features = full is { } value ? new Dictionary<int, int> { [LegionProtocol.FullFan] = value } : [],
                FanTable = table, EcFan = rpm,
                UnknownOriginal = full is null || (_model.EcFan ? rpm is null : table is null)
            };
        }, token).ConfigureAwait(false);
    }

    private async ValueTask RestoreAsync(CancellationToken token, Deadline? budget = null)
    {
        if (_journal is null || _identity is null || _wmi is null)
        {
            return;
        }

        var live = _hardware.Identity();
        if (LegionIdentity.Binding(live) != LegionIdentity.Binding(_identity) || LegionGoModels.Match(live) != _model)
        {
            return;
        }

        var ecReleaseAttempted = false;
        foreach (var entry in _journal.Entries<LegionRecoveryState>())
        {
            if (!DeviceWriteBudget.IsAvailable(budget ?? Deadline.Never))
            {
                break;
            }

            if (entry.FirmwareIdentity != LegionIdentity.Binding(live))
            {
                await _journal.RetireAsync(entry.ServiceId, "firmware changed", token).ConfigureAwait(false);
                continue;
            }

            var state = entry.OriginalState;
            try
            {
                // Non-custom OEM modes own their limits. Rewriting those limits after the mode
                // can return Lenovo firmware to Custom; restore raw limits only for a captured custom mode.
                foreach (var feature in state.Features.Where(_ => state.Mode is null or 255))
                {
                    DeviceWriteBudget.Require(budget ?? Deadline.Never, "Lenovo recovery feature");
                    _wmi.WriteFeature(feature.Key, feature.Value);
                }

                if (state.Mode is { } mode)
                {
                    _wmi.WriteMode(mode);
                }

                if (state.FanTable is { } table)
                {
                    _wmi.WriteFanTable(table);
                }

                if (entry.ServiceId == "fans" && _model!.EcFan)
                {
                    if (_ec is null)
                    {
                        continue;
                    }

                    // HC LegionGo2.Close releases the override with zero even when its prior RPM was unreadable.
                    ecReleaseAttempted = true;
                    _ec.WriteUInt16(0xC6C8, 0);
                    _fansChanged = false;
                }

                await _journal.SetStatusAsync(entry.ServiceId,
                        DeviceRecoveryStatus.RestoredUnverified,
                        token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "legion-recovery", ex.Message);
                await _journal
                    .SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        if (!ecReleaseAttempted && _fansChanged && _model!.EcFan && _ec is not null &&
            DeviceWriteBudget.IsAvailable(budget ?? Deadline.Never))
        {
            token.ThrowIfCancellationRequested();
            _ec.WriteUInt16(0xC6C8, 0);
            _fansChanged = false;
        }

        _fanMode = "automatic";
    }
}
