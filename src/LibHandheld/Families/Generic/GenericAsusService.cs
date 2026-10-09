using LibHandheld.Families.RogAlly;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Generic;

/// <summary>The reviewed ASUS System Control Interface remains usable without pretending an ASUS laptop is an Ally.</summary>
internal sealed class GenericAsusService(
    ProfiledFeatureContext context,
    AsusAuraController? aura = null,
    bool firmwareControls = true) : ProfiledFeatureService("asus-firmware")
{
    private static readonly IReadOnlyList<CapabilityDescriptor> Surface =
    [
        new()
        {
            CapabilityId = CapabilityIds.Scenario, Role = CapabilityRole.ScenarioMode,
            ValueKind = CapabilityValueKind.Choice,
            Display = Label("Performance mode"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices =
            [
                new CapabilityChoice("performance", Label("Performance")),
                new CapabilityChoice("turbo", Label("Turbo")), new CapabilityChoice("silent", Label("Silent"))
            ]
        },
        new()
        {
            CapabilityId = CapabilityIds.ChargeLimit, Role = CapabilityRole.ChargeLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = Label("Battery charge ceiling"), Minimum = 40, Maximum = 100, Step = 1,
            Unit = CapabilityUnit.Percent,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = CapabilityProfileScope.GlobalOnly
        },
        Fan("cpu", "CPU fan curve"), Fan("gpu", "GPU fan curve"),
        new()
        {
            CapabilityId = "asus.xg-enabled", Role = CapabilityRole.GenericToggle,
            ValueKind = CapabilityValueKind.Boolean,
            Display = Label("XG Mobile enabled"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = CapabilityProfileScope.GlobalOnly
        },
        new()
        {
            CapabilityId = "asus.xg-lighting", Role = CapabilityRole.LightingPower,
            ValueKind = CapabilityValueKind.Boolean,
            Display = Label("XG Mobile lighting"), SupportsWrite = true, Persistence = CapabilityPersistence.Unknown
        },
        Fan("xg", "XG Mobile fan curve")
    ];

    private readonly AsusAuraController _aura = aura ?? new AsusAuraController();
    private readonly Dictionary<(string, string?), CapabilityValue> _observed = [];
    private readonly HashSet<(string, string?)> _uncertainFirmwareCommands = [];
    private IAsusAcpi? _acpi;
    private GenericRecoveryJournal? _journal;
    private bool _unknownOriginal;
    private AsusXgTransport? _xg;
    internal override long DescriptorGeneration => _aura.Generation;
    private IReadOnlyList<CapabilityDescriptor> FirmwareSurface => firmwareControls ? Surface : [];

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
        Array.AsReadOnly(FirmwareSurface.Concat(_aura.Descriptors).ToArray());

    internal override IReadOnlyList<CapabilityState> States => Array.AsReadOnly(FirmwareSurface.Select(descriptor =>
    {
        var xg = descriptor.CapabilityId.StartsWith("asus.xg", StringComparison.Ordinal) ||
                 descriptor.InstanceId == "xg";
        _observed.TryGetValue((descriptor.CapabilityId, descriptor.InstanceId), out var value);
        return new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
            Available = !_uncertainFirmwareCommands.Contains((descriptor.CapabilityId, descriptor.InstanceId))
                        && (xg ? _xg?.Connected == true : _acpi is not null),
            ObservedValue = value,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
            DescriptorGeneration = 0, CycleGeneration = 0
        };
    }).Concat(_aura.States).ToArray());

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (firmwareControls)
        {
            var acpi = new WindowsAsusAcpi();
            if (acpi.TryOpen())
            {
                _acpi = acpi;
            }
            else
            {
                acpi.Dispose();
            }

            _xg = AsusXgTransport.Open();
        }

        _aura.Refresh(token);
        if (firmwareControls)
        {
            _journal = await GenericRecoveryJournal.Open(Path.Combine(context.StateDirectory, "asus-firmware"), token)
                .ConfigureAwait(false);
        }

        if (_journal is { } journal)
        {
            foreach (var entry in journal.OutstandingEntries)
            {
                if (entry.FirmwareIdentity != context.Firmware)
                {
                    await journal.SetStatusAsync(entry.ServiceId, DeviceRecoveryStatus.RestoredVerified, token)
                        .ConfigureAwait(false);
                }
                else if (entry.Status == DeviceRecoveryStatus.Pending && _acpi is not null)
                {
                    await RestoreAsync(entry.ServiceId, token).ConfigureAwait(false);
                }
            }
        }

        await RefreshAsync(token).ConfigureAwait(false);
        return Set(
            _acpi is not null || _xg is not null || _aura.Available
                ? DeviceServiceState.Owned
                : DeviceServiceState.Passive,
            _acpi is null && _xg is null && !_aura.Available
                ? Missing("The ASUS firmware and Aura interfaces are unavailable.")
                : null);
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _aura.Refresh(token);
        if (_acpi is not null)
        {
            if (AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(AsusAcpiId.PerformanceMode), out var mode) &&
                mode is >= 0 and <= 2)
            {
                _observed[(CapabilityIds.Scenario, null)] = CapabilityValue.Choice(Mode(mode));
            }

            if (AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(AsusAcpiId.ChargeLimit), out var charge) &&
                charge is >= 40 and <= 100)
            {
                _observed[(CapabilityIds.ChargeLimit, null)] = CapabilityValue.Integer(charge);
            }
        }

        if (_xg?.ReadEnabled() is { } enabled)
        {
            _observed[("asus.xg-enabled", null)] = CapabilityValue.Boolean(enabled);
        }

        return ValueTask.CompletedTask;
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_aura.Handles(command))
        {
            return _aura.Execute(command, token);
        }

        if (!firmwareControls)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "This accessory service owns only discovered Aura modules.");
        }

        if (_uncertainFirmwareCommands.Contains((command.CapabilityId, command.InstanceId)))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted,
                "The previous firmware write was uncertain and is not repeated automatically.");
        }

        var dispatched = false;
        try
        {
            var value = command.RequestedValue!;
            if (command.CapabilityId is "asus.xg-enabled" or "asus.xg-lighting" || command.InstanceId == "xg")
            {
                if (_xg?.Connected != true)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "XG Mobile is disconnected.");
                }

                token.ThrowIfCancellationRequested();
                dispatched = true;
                if (command.CapabilityId == "asus.xg-enabled")
                {
                    _xg.Enable(value.BooleanValue!.Value);
                }
                else if (command.CapabilityId == "asus.xg-lighting")
                {
                    _xg.Light(value.BooleanValue!.Value);
                }
                else
                {
                    if (!AllyFanCapability.TryEncode(value.CurveValue, out var curve, out var error))
                    {
                        return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                    }

                    _xg.Fan(curve);
                }
            }
            else
            {
                if (_acpi is null)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "ATKACPI is unavailable.");
                }

                if (command.CapabilityId == CapabilityIds.ChargeLimit)
                {
                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _acpi.Write(AsusAcpiId.ChargeLimit, checked((uint)value.IntegerValue!.Value));
                }
                else if (command.CapabilityId == CapabilityIds.Scenario)
                {
                    await CaptureAsync("mode", [0], OriginalMode(), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _acpi.Write(AsusAcpiId.PerformanceMode, checked((uint)Mode(value.ChoiceValue!)));
                }
                else if (command.CapabilityId == CapabilityIds.FanCurve)
                {
                    if (!AllyFanCapability.TryEncode(value.CurveValue, out var curve, out var error))
                    {
                        return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                    }

                    var id = command.InstanceId == "gpu" ? AsusAcpiId.GpuFanCurve : AsusAcpiId.CpuFanCurve;
                    var mode = OriginalMode();
                    byte[]? original = null;
                    if (mode is not null)
                    {
                        var read = _acpi.ReadBuffer(id, AsusAcpiProtocol.CurveSelector(mode[0]));
                        if (AsusAcpiProtocol.IsValidCurve(read))
                        {
                            original = [mode[0], .. read];
                        }
                    }

                    await CaptureAsync("fan-" + command.InstanceId,
                        [command.InstanceId == "gpu" ? (ushort)2 : (ushort)1], original, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _acpi.WriteBuffer(id, curve);
                }
                else
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "Unknown ASUS firmware control.");
                }
            }

            _observed[(command.CapabilityId, command.InstanceId)] = value;
            return CommandResults.Unverified(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _uncertainFirmwareCommands.Add((command.CapabilityId, command.InstanceId));
            }

            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message,
                    RollbackResult.NotRequired)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    private byte[]? OriginalMode()
    {
        return _acpi is not null && AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(AsusAcpiId.PerformanceMode),
                                     out var mode)
                                 && mode is >= 0 and <= 2
            ? [(byte)mode]
            : null;
    }

    private async ValueTask CaptureAsync(string id, ushort[] addresses, byte[]? original, CancellationToken token)
    {
        if (_journal!.EntryFor(id) is { } prior)
        {
            if (prior.Status != DeviceRecoveryStatus.Pending)
            {
                await _journal.BeginAsync(id, context.Firmware, prior.OriginalState, token).ConfigureAwait(false);
            }

            return;
        }

        if (original is null)
        {
            _unknownOriginal = true;
            return;
        }

        await _journal.BeginAsync(id, context.Firmware,
            new GenericRecoveryState { Addresses = addresses, Values = original }, token).ConfigureAwait(false);
    }

    private async ValueTask RestoreAsync(string id, CancellationToken token)
    {
        if (_acpi is null || _journal?.PendingOriginalFor(id) is not { } original)
        {
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (id == "mode")
            {
                _acpi.Write(AsusAcpiId.PerformanceMode, original.Values[0]);
            }
            else
            {
                var current = OriginalMode();
                _acpi.Write(AsusAcpiId.PerformanceMode, original.Values[0]);
                _acpi.WriteBuffer(id == "fan-gpu" ? AsusAcpiId.GpuFanCurve : AsusAcpiId.CpuFanCurve,
                    original.Values.AsSpan(1));
                if (current is not null)
                {
                    _acpi.Write(AsusAcpiId.PerformanceMode, current[0]);
                }
                else
                {
                    _unknownOriginal = true;
                }
            }

            await _journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            foreach (var id in new[] { "fan-cpu", "fan-gpu", "mode" })
            {
                await RestoreAsync(id, token).ConfigureAwait(false);
            }

            return _unknownOriginal || _journal?.OutstandingEntries.Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("ASUS temporary settings were restored without complete readback."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            _acpi?.Dispose();
            _acpi = null;
            _xg?.Dispose();
            _xg = null;
            _aura.Close();
            _observed.Clear();
        }
    }

    private static string Mode(int mode)
    {
        return mode switch { 0 => "performance", 1 => "turbo", _ => "silent" };
    }

    private static int Mode(string mode)
    {
        return mode switch
        {
            "performance" => 0, "turbo" => 1, "silent" => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = text };
    }

    private static CapabilityDescriptor Fan(string instance, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.FanCurve, InstanceId = instance, Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve,
            Display = Label(label), Minimum = 0, Maximum = 100, Unit = CapabilityUnit.Percent, SupportsWrite = true,
            Persistence = instance == "xg" ? CapabilityPersistence.Unknown : CapabilityPersistence.Volatile
        };
    }
}
