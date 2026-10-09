using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.LegionGo;

internal sealed partial class LegionGoDeviceEngine
{
    private async ValueTask<CapabilityCommandResult> ExecuteCoreAsync(CapabilityCommand command,
        CancellationToken token)
    {
        var surface = _surface!;
        if (command.ExpectedCycleGeneration != _cycle || command.ExpectedDescriptorGeneration != surface.Generation)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.GenerationChanged,
                "The Lenovo capability generation changed.");
        }

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
            return CommandResults.Rejected(command, CapabilityReasonCode.GenerationChanged,
                "The Lenovo machine or BIOS changed.");
        }

        var descriptor = surface.Descriptors.SingleOrDefault(d =>
            d.CapabilityId == command.CapabilityId && d.InstanceId == command.InstanceId);
        var value = command.RequestedValue;
        var action = descriptor?.SupportsAction == true && descriptor.ValueKind == CapabilityValueKind.None;
        if (descriptor is null || (!action && (!descriptor.SupportsWrite || value is null ||
                                               !CapabilityValueValidation.ValueMatches(value, descriptor, out _))))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "The Lenovo value does not fit its native descriptor.");
        }

        value ??= CapabilityValue.None();
        if (_journal?.FailureReason is { } failure && descriptor.Persistence == CapabilityPersistence.Volatile)
        {
            return CommandResults.Rejected(command, failure);
        }

        if (descriptor.SectionId == DeviceSections.RgbId ||
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
                    break;
                case CapabilityIds.ChargeLimit:
                    dispatched = true;
                    _wmi!.WriteFeature(LegionProtocol.ChargeProtection, value.BooleanValue == true ? 1 : 0);
                    break;
                case CapabilityIds.FanCurve:
                    await CaptureFansAsync(writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    if (_model.EcFan)
                    {
                        _fanCurve = value.CurveValue.ToArray();
                        _fanCurveFaulted = false;
                        _fanMode = "custom";
                        var temperature = _wmi!.ReadFeature(LegionProtocol.Temperature) ?? value.CurveValue[0].Input;
                        var rpm = LegionProtocol.FanPercentToRpm(CurveAt(value.CurveValue, temperature));
                        _ec!.WriteUInt16(0xC6C8, (ushort)rpm);
                        _lastFanRpm = rpm;
                    }
                    else
                    {
                        var table = Enumerable.Range(1, 10).Select(i => (ushort)CurveAt(value.CurveValue, i * 10))
                            .ToArray();
                        _wmi!.WriteFanTable(table);
                        _fanMode = "custom";
                    }

                    Remember(CapabilityIds.FanMode, CapabilityValue.Choice(_fanMode));
                    break;
                case FanDuty:
                    await CaptureFansAsync(writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    var targetRpm = LegionProtocol.FanPercentToRpm(value.IntegerValue!.Value);
                    _ec!.WriteUInt16(0xC6C8, (ushort)targetRpm);
                    _lastFanRpm = targetRpm;
                    _fanCurve = null;
                    _fanCurveFaulted = false;
                    _fanMode = "custom";
                    Remember(CapabilityIds.FanMode, CapabilityValue.Choice(_fanMode));
                    break;
                case CapabilityIds.FanMode:
                    await CaptureFansAsync(writeToken).ConfigureAwait(false);
                    writeToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    _wmi!.WriteFeature(LegionProtocol.FullFan, value.ChoiceValue == "full-speed" ? 1 : 0);
                    if (value.ChoiceValue == "automatic")
                    {
                        if (_model.EcFan)
                        {
                            _ec!.WriteUInt16(0xC6C8, 0);
                            _lastFanRpm = 0;
                        }
                        else if (_journal!.OriginalStateFor("fans")?.FanTable is { } original)
                        {
                            _wmi.WriteFanTable(original);
                        }
                        else
                        {
                            _wmi.WriteFanTable([44, 48, 55, 60, 71, 79, 87, 87, 100, 100]);
                        }
                    }

                    _fanMode = value.ChoiceValue!;
                    _fanCurveFaulted = false;
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
                    if (command.CapabilityId == CapabilityIds.LightingBrightness)
                    {
                        _brightness = value.IntegerValue!.Value;
                    }
                    else if (command.CapabilityId == CapabilityIds.LightingSpeed)
                    {
                        _speed = value.IntegerValue!.Value;
                    }
                    else if (command.CapabilityId == CapabilityIds.LightingEffect)
                    {
                        _effect = value.ChoiceValue!;
                    }
                    else if (command.InstanceId == "right")
                    {
                        _rightColor = value.ColorValue!.Value;
                    }
                    else
                    {
                        _leftColor = value.ColorValue!.Value;
                    }

                    dispatched = true;
                    await _hid!.WriteAsync(LegionProtocol.Rgb(_model, 3, _effect, _leftColor, _brightness, _speed),
                        writeToken).ConfigureAwait(false);
                    if (_model.Slim)
                    {
                        await _hid.WriteAsync(LegionProtocol.SlimReport([0x10, 2, 3]), writeToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _hid.WriteAsync(LegionProtocol.Rgb(_model, 4, _effect, _rightColor, _brightness, _speed),
                            writeToken).ConfigureAwait(false);
                        await _hid.WriteAsync([5, 6, 0x73, 2, 3, 3, 1], writeToken).ConfigureAwait(false);
                        await _hid.WriteAsync([5, 6, 0x73, 2, 4, 3, 1], writeToken).ConfigureAwait(false);
                    }

                    foreach (var report in LegionProtocol.LightingEnabled(_model, _lightingEnabled))
                    {
                        await _hid.WriteAsync(report, writeToken).ConfigureAwait(false);
                    }

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

            // Readback can improve evidence but never controls whether the write is admitted.
            try
            {
                await RefreshAsync(command.CapabilityId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return CommandResults.Unverified(command, ex.Message);
            }

            return descriptor.SupportsRead && VerifyReadback(command, value)
                ? CommandResults.Verified(command, value)
                : CommandResults.Unverified(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_model.EcFan && command.CapabilityId is FanDuty or CapabilityIds.FanCurve)
            {
                _fanCurveFaulted = true;
            }

            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, ex.Message,
                    RollbackResult.NotRequired)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, ex.Message);
        }
    }

    private void Remember(string id, CapabilityValue value, string? instance = null)
    {
        _observed[(id, instance)] = value;
        _written.Add((id, instance));
    }

    private bool VerifyReadback(CapabilityCommand command, CapabilityValue value)
    {
        switch (command.CapabilityId)
        {
            case CapabilityIds.PowerSustained:
            case CapabilityIds.PowerBoost:
                if (!DevicePowerPair.TryResolve(command, _surface!.Descriptors, out var sustained, out var boost,
                        out _))
                {
                    return false;
                }

                return _wmi!.ReadFeature(LegionProtocol.LongPower) == sustained &&
                       _wmi.ReadFeature(LegionProtocol.CrossPower) == sustained &&
                       _wmi.ReadFeature(LegionProtocol.ShortPower) == boost &&
                       _wmi.ReadFeature(LegionProtocol.PeakPower) == boost;
            case CapabilityIds.Scenario: return _wmi!.ReadMode() == ModeValue(value.ChoiceValue!);
            case CapabilityIds.ChargeLimit:
                return _wmi!.ReadFeature(LegionProtocol.ChargeProtection) == (value.BooleanValue == true ? 1 : 0);
            case CapabilityIds.FanCurve:
                return !_model!.EcFan && _wmi!.ReadFanTable()?.SequenceEqual(Enumerable.Range(1, 10)
                    .Select(i => (ushort)CurveAt(value.CurveValue, i * 10))) == true;
            default: return false;
        }
    }

    private async ValueTask CapturePowerAsync(string service, CancellationToken token)
    {
        if (_journal!.EntryFor(service) is { Status: DeviceRecoveryStatus.Pending } entry &&
            entry.FirmwareIdentity == LegionIdentity.Binding(_identity!))
        {
            return;
        }

        Dictionary<int, int> values = [];
        foreach (var id in new[]
                 {
                     LegionProtocol.LongPower, LegionProtocol.CrossPower, LegionProtocol.ShortPower,
                     LegionProtocol.PeakPower
                 })
        {
            if (_wmi!.ReadFeature(id) is { } value && value is > 0 and <= 255)
            {
                values[id] = value;
            }
        }

        var observedMode = _wmi!.ReadMode();
        var mode = observedMode is { } known && ModeName(known) is not null ? observedMode : null;
        await _journal.BeginAsync(service, LegionIdentity.Binding(_identity!), new LegionRecoveryState
                { Features = values, Mode = mode, UnknownOriginal = values.Count != 4 || mode is null }, token)
            .ConfigureAwait(false);
    }

    private async ValueTask CaptureFansAsync(CancellationToken token)
    {
        if (_journal!.EntryFor("fans") is { Status: DeviceRecoveryStatus.Pending } entry &&
            entry.FirmwareIdentity == LegionIdentity.Binding(_identity!))
        {
            return;
        }

        var full = _wmi!.ReadFeature(LegionProtocol.FullFan);
        var table = _model!.EcFan ? null : _wmi.ReadFanTable();
        ushort? rpm = null;
        if (_model.EcFan && _ec is not null)
        {
            try
            {
                rpm = _ec.ReadUInt16(0xC6C8);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _host?.Trace(DeviceTraceLevel.Warn, "legion-recovery", $"Fan original unreadable: {ex.Message}");
            }
        }

        await _journal.BeginAsync("fans", LegionIdentity.Binding(_identity!), new LegionRecoveryState
        {
            Features = full is { } value ? new Dictionary<int, int> { [LegionProtocol.FullFan] = value } : [],
            FanTable = table, EcFan = rpm,
            UnknownOriginal = full is null || (_model.EcFan ? rpm is null : table is null)
        }, token).ConfigureAwait(false);
    }

    private async ValueTask RestoreAsync(CancellationToken token, Deadline? budget = null)
    {
        if (_journal is null || _journal.FailureReason is not null || _identity is null || _wmi is null)
        {
            return;
        }

        var live = _hardware.Identity();
        if (LegionIdentity.Binding(live) != LegionIdentity.Binding(_identity) || LegionGoModels.Match(live) != _model)
        {
            return;
        }

        foreach (var entry in _journal.OutstandingEntries.Where(e => e.Status == DeviceRecoveryStatus.Pending))
        {
            if (!DeviceWriteBudget.IsAvailable(budget ?? Deadline.Never))
            {
                break;
            }

            if (entry.FirmwareIdentity != LegionIdentity.Binding(live))
            {
                continue;
            }

            var state = entry.OriginalState;
            var verified = !state.UnknownOriginal;
            try
            {
                // Restore mode before the captured limits because a mode transition resets those limits.
                if (state.Mode is { } mode)
                {
                    _wmi.WriteMode(mode);
                    verified &= _wmi.ReadMode() == mode;
                }

                foreach (var feature in state.Features)
                {
                    DeviceWriteBudget.Require(budget ?? Deadline.Never, "Lenovo recovery feature");
                    _wmi.WriteFeature(feature.Key, feature.Value);
                    verified &= _wmi.ReadFeature(feature.Key) == feature.Value;
                }

                if (state.FanTable is { } table)
                {
                    _wmi.WriteFanTable(table);
                    verified &= _wmi.ReadFanTable()?.SequenceEqual(table) == true;
                }

                if (state.EcFan is { } rpm)
                {
                    if (_ec is null)
                    {
                        continue;
                    }

                    _ec.WriteUInt16(0xC6C8, rpm);
                    verified &= _ec.ReadUInt16(0xC6C8) == rpm;
                }

                await _journal.SetStatusAsync(entry.ServiceId,
                        verified ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoredUnverified,
                        token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _host?.Trace(DeviceTraceLevel.Warn, "legion-recovery", ex.Message);
                await _journal
                    .SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        _fanCurve = null;
        _fanMode = "automatic";
        _lastFanRpm = null;
    }
}
