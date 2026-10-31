using System.Diagnostics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Ayaneo;

/// <summary>CEc and CEii RGB/bypass mechanics share state and lifecycle while retaining their register codecs.</summary>
internal sealed class AyaneoNativeService(
    AyaneoModel model,
    ProfiledFeatureContext context,
    Func<AyaneoEcProtocol, IEmbeddedController>? open = null) : ProfiledFeatureService("ayaneo-native")
{
    internal const string BypassId = "battery.charge-bypass";
    internal const string LightPowerId = "lighting.power";
    internal const string SecondaryId = "display.secondary-brightness";
    internal const string ModuleLeftId = "controller.module-left";
    internal const string ModuleRightId = "controller.module-right";
    internal const string MappingId = "controller.configure-oem-mapping";
    internal const string EjectId = "controller.eject-module";
    internal const string VibrationId = "controller.vibration-strength";
    private readonly DeviceRecoveryJournal _journal = context.Journal;
    private readonly Dictionary<(string Id, string? Instance), CapabilityValue> _observed = new();
    private readonly AyaneoSuperJoy _superJoy = new();
    private int _brightness = 100;
    private bool _controllerEnabled = context.ControllerManagementEnabled;
    private IEmbeddedController? _ec;
    private string _effect = "solid";
    private bool _ejectionPending;
    private bool _enabled = true;
    private string _firmware = context.Firmware;
    private long _lastModuleQuery;
    private byte? _lastPowerSource;
    private int _left;
    private bool _lightingWritten;
    private bool _modulesRemoved;
    private int _right;
    private IReadOnlyList<CapabilityState> _states = [];
    private byte _vibration = 0x20;

    public override bool Suspendable => true;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = Describe(model);
    internal override IReadOnlyList<CapabilityState> States => _states;
    private ushort OwnerAddress => model.Protocol == AyaneoEcProtocol.CEii ? (ushort)0xD187 : (ushort)0xBF;

    internal override async ValueTask DisposeAsync()
    {
        try
        {
            await _superJoy.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _ec?.Dispose();
            _ec = null;
        }
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _firmware = cycle.Identity.Firmware;

        _ec = open?.Invoke(model.Protocol) ?? (model.Protocol == AyaneoEcProtocol.CEii
            ? PawnIoEmbeddedController.OpenIndexed()
            : PawnIoEmbeddedController.OpenAcpi());
        await _journal.ReconcileAsync<AyaneoRecoveryState>("lighting-owner", _firmware,
            (original, ct) => RestoreOwnerAsync(original.Owner, ct), token).ConfigureAwait(false);

        if (model.Lighting != AyaneoLighting.None)
        {
            await _journal.BeginAsync("lighting-owner", cycle.Identity.Firmware,
                () => new AyaneoRecoveryState { Owner = _ec.ReadByte(OwnerAddress) }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ec.WriteByte(OwnerAddress, model.Protocol == AyaneoEcProtocol.CEii ? (byte)0xA5 : (byte)0xFE);
            if (model.Lighting == AyaneoLighting.SuperJoy)
            {
                _superJoy.Open();
            }

            if (_lightingWritten)
            {
                await ApplyLightingAsync(token).ConfigureAwait(false);
            }
        }

        if (model.ModularController)
        {
            await _journal.ReconcileAsync<AyaneoRecoveryState>("controller-power", _firmware,
                (original, ct) => RestorePowerAsync(original.Owner, ct), token).ConfigureAwait(false);
            if (_controllerEnabled)
            {
                await AcquireControllerPowerAsync(token).ConfigureAwait(false);
            }
        }

        await RefreshAsync(token).ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    internal override async ValueTask RefreshAsync(CancellationToken token)
    {
        if (_ec is null)
        {
            PublishStates();
            return;
        }

        var powerSource = AyaneoPowerSource.Read();
        if (_lastPowerSource == 1 && powerSource == 0 && _lightingWritten)
        {
            try
            {
                await ApplyLightingAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                DeviceTrace.Change("ayaneo-native", "power-led-restore", exception.Message, DeviceTraceLevel.Warn);
            }
        }

        if (powerSource is not null)
        {
            _lastPowerSource = powerSource;
        }

        if (model.ModularController && _controllerEnabled && Environment.TickCount64 - _lastModuleQuery >= 2000)
        {
            _lastModuleQuery = Environment.TickCount64;
            try
            {
                var connected = (byte)(~_ec.ReadByte(0x2F) & 3);
                Observe("controller.modules", null, CapabilityValue.Choice(connected switch
                {
                    3 => "both", 1 => "left", 2 => "right", _ => "none"
                }));
                if (_ejectionPending && connected != 3)
                {
                    _modulesRemoved = true;
                }

                if (_ejectionPending && _modulesRemoved && connected == 3)
                {
                    _ejectionPending = false;
                    _modulesRemoved = false;
                    await AcquireControllerPowerAsync(token).ConfigureAwait(false);
                }

                if (!_ejectionPending && _superJoy.Open())
                {
                    var report = await _superJoy.QueryAsync(token).ConfigureAwait(false);
                    Observe(ModuleLeftId, null, CapabilityValue.Text(AyaneoSuperJoy.LeftModule(report[33])));
                    Observe(ModuleRightId, null, CapabilityValue.Text(AyaneoSuperJoy.RightModule(report[34])));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                DeviceTrace.Change("ayaneo-native", "module-query", exception.Message, DeviceTraceLevel.Warn);
            }
        }

        PublishStates();
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The native command was cancelled before dispatch.");
        }

        try
        {
            return await ExecuteCoreAsync(command, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The shared dispatcher has already admitted this operation. A transport failure can be
            // partial, so neither rollback nor retry is inferred from the failure.
            if (command.CapabilityId.StartsWith("lighting.", StringComparison.Ordinal))
            {
                _lightingWritten = false;
                foreach (var key in _observed.Keys
                             .Where(key => key.Id.StartsWith("lighting.", StringComparison.Ordinal)).ToArray())
                {
                    _observed.Remove(key);
                }
            }
            else
            {
                _observed.Remove((command.CapabilityId, command.InstanceId));
            }

            PublishStates();
            return CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    private async ValueTask<CapabilityCommandResult> ExecuteCoreAsync(CapabilityCommand command,
        CancellationToken token)
    {
        if (_ec is null || State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "AYANEO EC ownership is not active.");
        }

        token.ThrowIfCancellationRequested();
        switch (command.CapabilityId)
        {
            case BypassId:
                var bypass = command.RequestedValue!.BooleanValue!.Value;
                // Never gate a supported write on successful readback or repeat an uncertain write.
                _ec.WriteByte(model.Protocol == AyaneoEcProtocol.CEii ? (ushort)0xD1D1 : (ushort)0x1E,
                    model.Protocol == AyaneoEcProtocol.CEii ? bypass ? (byte)1 : (byte)0x65 :
                    bypass ? (byte)0x55 : (byte)0xAA);
                break;
            case SecondaryId:
                _ec.Transaction(() =>
                {
                    _ec.WriteByte(0xFF, 0);
                    _ec.WriteByte(0x4E, (byte)(command.RequestedValue!.IntegerValue!.Value * 255 / 100));
                    return true;
                });
                break;
            case LightPowerId:
                _enabled = command.RequestedValue!.BooleanValue!.Value;
                await ApplyLightingAsync(token).ConfigureAwait(false);
                break;
            case CapabilityIds.LightingBrightness:
                _brightness = command.RequestedValue!.IntegerValue!.Value;
                await ApplyLightingAsync(token).ConfigureAwait(false);
                break;
            case CapabilityIds.LightingEffect:
                _effect = command.RequestedValue!.ChoiceValue!;
                await ApplyLightingAsync(token).ConfigureAwait(false);
                break;
            case CapabilityIds.LightingColor:
                if (command.InstanceId == "left")
                {
                    _left = command.RequestedValue!.ColorValue!.Value;
                }
                else
                {
                    _right = command.RequestedValue!.ColorValue!.Value;
                }

                await ApplyLightingAsync(token).ConfigureAwait(false);
                break;
            case MappingId:
                if (!_controllerEnabled || !_superJoy.Open())
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "Controller management or the modular HID interface is unavailable.");
                }

                await ConfigureSafeOemMappingAsync(token).ConfigureAwait(false);
                break;
            case VibrationId:
                if (!_controllerEnabled)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "Controller management is disabled.");
                }

                _vibration = command.RequestedValue!.ChoiceValue switch
                {
                    "low" => 0x10, "medium" => 0x20, "high" => 0x30, "disabled" => 0x40,
                    _ => throw new InvalidOperationException("Unknown vibration strength.")
                };
                await ApplyLightingAsync(token).ConfigureAwait(false);
                break;
            case EjectId:
                if (!_controllerEnabled || !_superJoy.Open())
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "Controller management or the modular HID interface is unavailable.");
                }

                await EjectAsync(command.InstanceId!, token).ConfigureAwait(false);
                break;
            default:
                return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                    "The native capability is not declared by this model.");
        }

        if (command.RequestedValue is { } value)
        {
            Observe(command.CapabilityId, command.InstanceId, value);
        }

        PublishStates();
        return CommandResults.Applied(command);
    }

    private async ValueTask ApplyLightingAsync(CancellationToken token)
    {
        // A partial write clears reapplication admission. Suspend/resume cannot repeat an uncertain
        // command merely because a previous successful command used the same lighting owner.
        _lightingWritten = false;
        if (model.Lighting == AyaneoLighting.SuperJoy)
        {
            if (!_superJoy.Open())
            {
                throw new IOException("The SuperJoy RGB interface is absent or ambiguous.");
            }

            await _superJoy.ExchangeAsync(AyaneoSuperJoy.Lighting(_left, _right, _brightness, _enabled, _effect,
                vibration: _vibration), token).ConfigureAwait(false);
            // The AYA logo remains on the CEc EC, even though the stick rings use SuperJoy.
            SetGroupPower(4);
            WriteZone(4, 4, _left, false, true);
        }
        else
        {
            SetGroupPower(3);
            if (model.Lighting != AyaneoLighting.CEii)
            {
                SetGroupPower(4);
            }

            for (var zone = 1; zone <= 4; zone++)
            {
                WriteZone(1, zone, _left, model.Lighting == AyaneoLighting.Kun);
                WriteZone(2, zone, _right, model.Lighting == AyaneoLighting.Kun);
            }

            if (model.Lighting != AyaneoLighting.CEii)
            {
                WriteZone(4, 4, _left, false, true);
            }
        }

        _lightingWritten = true;
        Observe(LightPowerId, null, CapabilityValue.Boolean(_enabled));
        Observe(CapabilityIds.LightingBrightness, null, CapabilityValue.Integer(_brightness));
        Observe(CapabilityIds.LightingColor, "left", CapabilityValue.Color(_left));
        Observe(CapabilityIds.LightingColor, "right", CapabilityValue.Color(_right));
        if (model.Lighting == AyaneoLighting.SuperJoy)
        {
            Observe(CapabilityIds.LightingEffect, null, CapabilityValue.Choice(_effect));
        }
    }

    private void SetGroupPower(byte group)
    {
        WriteRgb(group, 2, _enabled ? (byte)0x80 : (byte)0xC0);
    }

    private void WriteZone(byte group, int zone, int rgb, bool kun, bool logo = false)
    {
        var red = (byte)(rgb >> 16);
        var green = (byte)(rgb >> 8);
        var blue = (byte)rgb;
        var (first, second, third) = logo ? (blue, red, green)
            : kun ? zone switch
            {
                1 => (green, red, blue),
                2 => (green, blue, red),
                3 => (green, red, blue),
                4 => (green, blue, red),
                _ => (red, green, blue)
            } : (red, green, blue);
        var address = (byte)(zone * 3);
        WriteRgb(group, address, Scale(first));
        WriteRgb(group, (byte)(address + 1), Scale(second));
        WriteRgb(group, (byte)(address + 2), Scale(third));
    }

    private byte Scale(byte channel)
    {
        return (byte)(channel * 192 * _brightness / 255 / 100);
    }

    private void WriteRgb(byte group, byte command, byte argument)
    {
        if (model.Protocol == AyaneoEcProtocol.CEii)
        {
            if (group == 3)
            {
                WriteRgb(1, command, argument);
                WriteRgb(2, command, argument);
                return;
            }

            _ec!.Transaction(() =>
            {
                _ec.WriteByte(0xD187, 0xA5);
                _ec.WriteByte((ushort)((group == 1 ? 0xD1B0 : 0xD170) + command), argument);
                _ec.WriteByte(group == 1 ? (ushort)0xD1C6 : (ushort)0xD186, 1);
                Thread.Sleep(10);
                return true;
            });
        }
        else
        {
            CecRgb(group, command, argument);
            if (model.RgbConfirmation)
            {
                CecRgb(3, 0, 0);
            }
        }
    }

    private void CecRgb(byte group, byte command, byte argument)
    {
        _ec!.Transaction(() =>
        {
            _ec.WriteByte(0x6D, group);
            _ec.WriteByte(0xB1, command);
            _ec.WriteByte(0xB2, argument);
            _ec.WriteByte(0xBF, 0x10);
            Thread.Sleep(5);
            _ec.WriteByte(0xBF, 0xFE);
            return true;
        });
    }

    private async ValueTask ConfigureSafeOemMappingAsync(CancellationToken token)
    {
        // Intentional durable remapping. The device does not provide a row-table read protocol; never
        // fabricate an original table for teardown. The caller explicitly invokes this configuration.
        await _superJoy.SelectCustomAsync(true, token).ConfigureAwait(false);
        for (byte index = 0; index <= 32; index++)
        {
            var usage = index switch
            {
                12 => (byte)0x6E, 13 => (byte)0x6F, 16 => (byte)0x70, 17 => (byte)0x71,
                18 => (byte)0x6C, 19 => (byte)0x6D, 22 => (byte)0x72, 23 => (byte)0x73, 24 => (byte)0x6B, _ => (byte)0
            };
            await _superJoy.ExchangeAsync(AyaneoSuperJoy.Mapping(index, usage), token).ConfigureAwait(false);
        }

        await _superJoy.ExchangeAsync(AyaneoSuperJoy.Command(0x05), token, 1800).ConfigureAwait(false);
    }

    internal override async ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _controllerEnabled = enabled;
        // The vendor pipe also controls lighting, which remains usable when input integration is off.
        // Only mapping and module-management operations are admitted by the controller toggle.
        if (model.ModularController && _ec is not null)
        {
            if (enabled)
            {
                _ejectionPending = false;
                _modulesRemoved = false;
                await AcquireControllerPowerAsync(token).ConfigureAwait(false);
            }
            else if (_journal.OriginalStateFor<AyaneoRecoveryState>("controller-power") is { } original)
            {
                await RestorePowerAsync(original.Owner, token).ConfigureAwait(false);
            }
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        var state = DeviceServiceState.Idle;
        try
        {
            foreach (var (id, restore) in new (string, Func<byte, CancellationToken, ValueTask>)[]
                     {
                         ("lighting-owner", RestoreOwnerAsync), ("controller-power", RestorePowerAsync)
                     })
            {
                if (_ec is not null && _journal.OriginalStateFor<AyaneoRecoveryState>(id) is { } original)
                {
                    await restore(original.Owner, token).ConfigureAwait(false);
                    if (_journal.EntryFor<AyaneoRecoveryState>(id) is not null)
                    {
                        state = DeviceServiceState.ReleasedUnverified;
                    }
                }
            }
        }
        finally
        {
            await _superJoy.DisposeAsync().ConfigureAwait(false);
            _ec?.Dispose();
            _ec = null;
            PublishStates();
        }

        return Set(state);
    }

    private async ValueTask RestoreOwnerAsync(byte original, CancellationToken token)
    {
        _ec!.WriteByte(OwnerAddress, original);
        await _journal.MarkRestoredAsync("lighting-owner", token).ConfigureAwait(false);
    }

    private async ValueTask AcquireControllerPowerAsync(CancellationToken token)
    {
        if ((~_ec!.ReadByte(0x2F) & 3) != 3 || _ejectionPending)
        {
            return;
        }

        // This takes ownership only while both modules are present. A pending uncertain write is never
        // resubmitted by observation; explicit re-enable or a completed removal/insertion starts a new action.
        await _journal.BeginAsync("controller-power", _firmware,
                () => new AyaneoRecoveryState { Owner = _ec.ReadByte(0x2D) }, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _ec.WriteByte(0x2D, 0xFF);
    }

    private async ValueTask RestorePowerAsync(byte original, CancellationToken token)
    {
        _ec!.WriteByte(0x2D, original);
        await _journal.MarkRestoredAsync("controller-power", token).ConfigureAwait(false);
    }

    private async ValueTask EjectAsync(string side, CancellationToken token)
    {
        var ec = _ec ?? throw new InvalidOperationException("AYANEO EC ownership is not active.");
        var bits = side switch
        {
            "left" => (byte)0x07, "right" => (byte)0x70, "both" => (byte)0x77,
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };
        _ejectionPending = true;
        _modulesRemoved = false;
        try
        {
            await _journal.BeginAsync("controller-power", _firmware,
                () => new AyaneoRecoveryState { Owner = ec.ReadByte(0x2D) }, token).ConfigureAwait(false);
            await CompleteEjectAsync(
                ct => _superJoy.ExchangeAsync(AyaneoSuperJoy.Lighting(_left, _right, _brightness, _enabled,
                    _effect, bits, _vibration), ct),
                ct => _superJoy.QueryAsync(ct),
                () => ec.WriteByte(0x2D, 0xFE), token).ConfigureAwait(false);
        }
        catch
        {
            // A failed or unconfirmed latch operation cannot authorize cutting controller power.
            _ejectionPending = false;
            _modulesRemoved = false;
            throw;
        }
        finally
        {
            await _superJoy.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async ValueTask CompleteEjectAsync(
        Func<CancellationToken, ValueTask<byte[]>> submit,
        Func<CancellationToken, ValueTask<byte[]>> query,
        Action powerDown,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? elapsed = null)
    {
        token.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        await submit(token).ConfigureAwait(false);
        delay ??= Task.Delay;
        var confirmed = false;
        // Status queries observe the admitted eject; they never resubmit its command.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var report = await query(CancellationToken.None).ConfigureAwait(false);
            await delay(TimeSpan.FromMilliseconds(400), CancellationToken.None).ConfigureAwait(false);
            if (report.Length == 65 && (report[20] & ~0x11) == 0)
            {
                confirmed = true;
                break;
            }
        }

        if (!confirmed)
        {
            throw new IOException("Module eject was not confirmed; controller power remains on.");
        }

        var remaining = TimeSpan.FromSeconds(3) - (elapsed?.Invoke()
                                                   ?? Stopwatch.GetElapsedTime(started));
        if (remaining > TimeSpan.Zero)
        {
            await delay(remaining, CancellationToken.None).ConfigureAwait(false);
        }

        powerDown();
    }

    private void Observe(string id, string? instance, CapabilityValue value)
    {
        _observed[(id, instance)] = value;
    }

    private void PublishStates()
    {
        _states = Array.AsReadOnly(Descriptors.Select(descriptor =>
        {
            var hasValue = _observed.TryGetValue((descriptor.CapabilityId, descriptor.InstanceId), out var value);
            var available = _ec is not null && (model.Lighting != AyaneoLighting.SuperJoy
                                                || descriptor.Role is not (CapabilityRole.LightingPower
                                                    or CapabilityRole.LightingBrightness
                                                    or CapabilityRole.LightingEffect
                                                    or CapabilityRole.LightingZoneColor)
                                                || _superJoy.IsOpen);
            if (descriptor.CapabilityId is ModuleLeftId or ModuleRightId or MappingId or EjectId or VibrationId)
            {
                available &= _controllerEnabled && _superJoy.IsOpen;
            }

            return ProviderControls.State(descriptor.CapabilityId, available, hasValue ? value : null,
                hasValue ? DateTimeOffset.UtcNow : null,
                available
                    ? null
                    : new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                        "The required AYANEO native interface is unavailable."), descriptor.InstanceId);
        }).ToArray());
    }


    internal static IReadOnlyList<CapabilityDescriptor> Describe(AyaneoModel model)
    {
        var descriptors = new List<CapabilityDescriptor>
        {
            Value(BypassId, CapabilityRole.ChargeBypass, "Bypass charging", CapabilityValueKind.Boolean) with
            {
                SupportsRead = true
            }
        };
        if (model.Lighting != AyaneoLighting.None)
        {
            descriptors.Add(Value(LightPowerId, CapabilityRole.LightingPower, "Lighting", CapabilityValueKind.Boolean));
            descriptors.Add(Value(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                    "Lighting brightness", CapabilityValueKind.Integer)
                with
                {
                    Minimum = 0, Maximum = 100, Step = 1, Unit = CapabilityUnit.Percent
                });
            descriptors.Add(Value(CapabilityIds.LightingColor, CapabilityRole.LightingZoneColor, "Left stick colour",
                    CapabilityValueKind.Color) with
                {
                    InstanceId = "left"
                });
            descriptors.Add(Value(CapabilityIds.LightingColor, CapabilityRole.LightingZoneColor, "Right stick colour",
                    CapabilityValueKind.Color) with
                {
                    InstanceId = "right"
                });
            if (model.Lighting == AyaneoLighting.SuperJoy)
            {
                descriptors.Add(Value(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect, "Lighting effect",
                        CapabilityValueKind.Choice)
                    with
                    {
                        Choices = CapabilityDescriptors.Choices(new[] { "solid", "pulse", "rainbow" })
                    });
            }
        }

        if (model.SecondaryDisplay)
        {
            descriptors.Add(Value(SecondaryId, CapabilityRole.GenericRange, "Secondary display brightness",
                    CapabilityValueKind.Integer)
                with
                {
                    Minimum = 0, Maximum = 100, Step = 1, Unit = CapabilityUnit.Percent, SupportsRead = true
                });
        }

        if (model.ModularController)
        {
            descriptors.Add(Value(VibrationId, CapabilityRole.GenericChoice, "Controller vibration strength",
                    CapabilityValueKind.Choice)
                with
                {
                    Choices = CapabilityDescriptors.Choices(new[] { "disabled", "low", "medium", "high" })
                });
            descriptors.Add(Value("controller.modules", CapabilityRole.GenericReadOnly, "Connected modules",
                    CapabilityValueKind.Choice)
                with
                {
                    SupportsRead = true, SupportsWrite = false,
                    Choices = CapabilityDescriptors.Choices(new[] { "both", "left", "right", "none" })
                });
            foreach (var side in new[] { (ModuleLeftId, "Left module"), (ModuleRightId, "Right module") })
            {
                descriptors.Add(Value(side.Item1, CapabilityRole.GenericReadOnly, side.Item2, CapabilityValueKind.Text)
                    with
                    {
                        SupportsRead = true, SupportsWrite = false, MaximumLength = 80
                    });
            }

            descriptors.Add(Value(MappingId, CapabilityRole.GenericAction, "Configure OEM controller keys",
                    CapabilityValueKind.None)
                with
                {
                    SupportsWrite = false, SupportsAction = true
                });
            foreach (var side in new[] { "left", "right", "both" })
            {
                descriptors.Add(Value(EjectId, CapabilityRole.GenericAction, "Eject " + side + " module",
                        CapabilityValueKind.None)
                    with
                    {
                        InstanceId = side, SupportsWrite = false, SupportsAction = true
                    });
            }
        }

        return Array.AsReadOnly(descriptors.ToArray());
    }

    private static CapabilityDescriptor Value(string id, CapabilityRole role, string label, CapabilityValueKind kind)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, Display = CapabilityDescriptors.Label(label), ValueKind = kind,
            SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
        };
    }
}

internal sealed record AyaneoRecoveryState
{
    public byte Owner { get; init; }
}
