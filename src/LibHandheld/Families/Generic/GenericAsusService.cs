using System.Globalization;
using LibHandheld.Contracts;
using LibHandheld.Families.RogAlly;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Generic;

/// <summary>The reviewed ASUS System Control Interface remains usable without pretending an ASUS laptop is an Ally.</summary>
internal sealed class GenericAsusService(
    ProfiledFeatureContext context,
    AsusAuraController? aura = null,
    bool firmwareControls = true,
    Func<IAsusAcpi>? openAcpi = null) : ProfiledFeatureService(firmwareControls ? "asus-firmware" : "asus-accessories")
{
    private static readonly IReadOnlyList<CapabilityDescriptor> Surface =
    [
        new()
        {
            CapabilityId = CapabilityIds.Scenario, Role = CapabilityRole.ScenarioMode,
            ValueKind = CapabilityValueKind.Choice,
            Display = CapabilityDescriptors.Label("Performance mode"), SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices =
            [
                new CapabilityChoice("performance", CapabilityDescriptors.Label("Performance")),
                new CapabilityChoice("turbo", CapabilityDescriptors.Label("Turbo")),
                new CapabilityChoice("silent", CapabilityDescriptors.Label("Silent"))
            ]
        },
        new()
        {
            CapabilityId = CapabilityIds.ChargeLimit, Role = CapabilityRole.ChargeLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = CapabilityDescriptors.Label("Battery charge ceiling"),
            Minimum = AllyChargeLimitCapability.MinimumPercent,
            Maximum = AllyChargeLimitCapability.MaximumPercent, Step = 1,
            Unit = CapabilityUnit.Percent,
            SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = CapabilityProfileScope.GlobalOnly
        },
        Fan("cpu", "CPU fan curve"), Fan("gpu", "GPU fan curve"),
        Duty("cpu"), Duty("gpu"),
        new()
        {
            CapabilityId = CapabilityIds.FanMode, Role = CapabilityRole.FanMode,
            ValueKind = CapabilityValueKind.Choice, Display = new CapabilityDisplay { Key = DisplayKey.FanMode },
            SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
            Choices =
            [
                new CapabilityChoice("automatic", CapabilityDescriptors.Label("Automatic")),
                new CapabilityChoice("custom", CapabilityDescriptors.Label("Custom"))
            ]
        },
        new()
        {
            CapabilityId = "asus.xg-enabled", InstanceId = "xg", Role = CapabilityRole.GenericToggle,
            ValueKind = CapabilityValueKind.Boolean,
            Display = CapabilityDescriptors.Label("XG Mobile enabled"), SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            ProfileScope = CapabilityProfileScope.GlobalOnly
        },
        new()
        {
            CapabilityId = "asus.xg-lighting", InstanceId = "xg", Role = CapabilityRole.LightingPower,
            ValueKind = CapabilityValueKind.Boolean,
            Display = CapabilityDescriptors.Label("XG Mobile lighting"), SupportsWrite = true,
            Persistence = CapabilityPersistence.Unknown
        },
        Fan("xg", "XG Mobile fan curve")
    ];

    private readonly AsusAuraController _aura = aura ?? new AsusAuraController();
    private readonly HashSet<string> _captureAttempted = [];
    private readonly HashSet<string> _changedFans = [];
    private readonly ObservedValues _observed = new();

    private IReadOnlyList<HidCollection>? _accessoryCollections;
    private IAsusAcpi? _acpi;
    private AllyChargeLimitCapability? _charge;
    private DeviceRecoveryJournal? _journal;
    private bool _topologyChanged;
    private bool _unknownOriginal;
    private AsusXgTransport? _xg;

    internal static IReadOnlyList<CapabilityRole> AccessoryRoles { get; } =
    [
        CapabilityRole.LightingBrightness, CapabilityRole.LightingEffect, CapabilityRole.LightingEffectSpeed,
        CapabilityRole.LightingZoneColor, CapabilityRole.LightingPower, CapabilityRole.GenericToggle,
        CapabilityRole.GenericChoice, CapabilityRole.FanCurve
    ];

    private bool AccessoryPresent => aura is not null || (_accessoryCollections is { } current
        ? current.Count > 0
        : MatchesAccessoryIdentity(context.Identity));

    private bool XgPresent => _accessoryCollections is { } current
        ? current.Any(collection => collection.ProductId is 0x1970 or 0x1A9A or 0x1BC1)
        : context.Identity.UsbEndpoints.Any(endpoint =>
            endpoint.VendorId.Equals("0B05", StringComparison.OrdinalIgnoreCase)
            && endpoint.ProductId.ToUpperInvariant() is "1970" or "1A9A" or "1BC1");

    internal override bool DescriptorsChanged => _topologyChanged || _aura.DescriptorsChanged;

    private IReadOnlyList<CapabilityDescriptor> FirmwareSurface => Surface.Where(descriptor =>
        descriptor.InstanceId == "xg" ? XgPresent : firmwareControls).ToArray();

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
        Array.AsReadOnly(FirmwareSurface.Concat(_aura.Descriptors).ToArray());

    internal override IReadOnlyList<CapabilityState> States => Array.AsReadOnly(FirmwareSurface.Select(descriptor =>
    {
        var xg = descriptor.CapabilityId.StartsWith("asus.xg", StringComparison.Ordinal) ||
                 descriptor.InstanceId == "xg";
        var value = _observed.Value(descriptor.CapabilityId, descriptor.InstanceId);
        return new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
            Available = xg ? _xg is not null : _acpi is not null,
            ObservedValue = value,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
            ObservedAt = _observed.Time(descriptor.CapabilityId, descriptor.InstanceId)
        };
    }).Concat(_aura.States).ToArray());

    public override bool Suspendable => true;

    internal static bool MatchesAccessoryIdentity(DeviceIdentitySnapshot identity)
    {
        return identity.UsbEndpoints.Any(endpoint =>
            ushort.TryParse(endpoint.VendorId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vendor) &&
            vendor == AsusAuraProtocol.Vendor
            && ushort.TryParse(endpoint.ProductId, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var product)
            && (AsusAuraProtocol.Supported(product) || product is 0x1970 or 0x1A9A));
    }

    internal void SetAccessoryCollections(IReadOnlyList<HidCollection> collections)
    {
        var hadXg = XgPresent;
        _accessoryCollections = collections;
        _topologyChanged |= hadXg != XgPresent;
    }

    internal override void AcknowledgeDescriptors()
    {
        _topologyChanged = false;
        _aura.AcknowledgeDescriptors();
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (firmwareControls)
        {
            var acpi = openAcpi?.Invoke() ?? new WindowsAsusAcpi();
            if (acpi.TryOpen())
            {
                _acpi = acpi;
                _charge = new AllyChargeLimitCapability(acpi);
            }
            else
            {
                acpi.Dispose();
            }
        }

        if (firmwareControls)
        {
            _journal = context.Journal;
            _captureAttempted.Clear();
        }

        if (_journal is { } journal)
        {
            foreach (var entry in journal.Entries<GenericRecoveryState>())
            {
                await journal.ReconcileAsync<GenericRecoveryState>(entry.ServiceId, context.Firmware,
                    (_, ct) => RestoreAsync(entry.ServiceId, ct), token).ConfigureAwait(false);
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
        if (AccessoryPresent)
        {
            try
            {
                var collections = aura is null
                    ? _accessoryCollections ?? AsusAuraTransport.DiscoverCollections()
                    : [];
                _aura.Refresh(token, aura is null ? AsusAuraTransport.Modules(collections) : null);
                if (XgPresent && _xg is null)
                {
                    _xg = AsusXgTransport.Open(collections);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
            {
                DeviceTrace.Change("asus-accessories", "discovery", exception.Message, DeviceTraceLevel.Warn);
            }
        }
        else
        {
            _aura.Close();
        }

        if (!XgPresent)
        {
            _xg?.Dispose();
            _xg = null;
        }

        Set(_acpi is not null || _xg is not null || _aura.Available
            ? DeviceServiceState.Owned
            : DeviceServiceState.Passive);

        return ValueTask.CompletedTask;
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_aura.Handles(command))
        {
            return _aura.Execute(command, token);
        }

        if (!firmwareControls && command.InstanceId != "xg")
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "This accessory service owns only discovered Aura modules.");
        }


        var dispatched = false;
        try
        {
            var value = command.RequestedValue!;
            if (command.CapabilityId is "asus.xg-enabled" or "asus.xg-lighting" || command.InstanceId == "xg")
            {
                if (_xg is null)
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
                    var result = _charge!.Apply(command, value.IntegerValue!.Value);
                    if (result.Outcome == CommandOutcome.Applied)
                    {
                        _observed.Written(command.CapabilityId, value, command.InstanceId);
                    }

                    return result;
                }

                if (command.CapabilityId == CapabilityIds.Scenario)
                {
                    await CaptureAsync("mode", [0], OriginalMode, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _acpi.Write(AsusAcpiId.PerformanceMode, AllyModels.ScenarioValue(value.ChoiceValue!));
                }
                else if (command.CapabilityId == "fan.duty")
                {
                    var fan = command.InstanceId == "gpu" ? AsusAcpiId.GpuFanCurve : AsusAcpiId.CpuFanCurve;
                    await CaptureAsync("mode", [0], OriginalMode, token).ConfigureAwait(false);
                    await CaptureFanAsync(command.InstanceId!, fan, token).ConfigureAwait(false);
                    var percent = value.IntegerValue!.Value;
                    var curve = AsusAcpiProtocol.FlatFanCurve(percent);
                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _changedFans.Add(command.InstanceId!);
                    _acpi.WriteBuffer(fan, curve);
                    _observed.Written(CapabilityIds.FanMode, CapabilityValue.Choice("custom"));
                }
                else if (command.CapabilityId == CapabilityIds.FanMode)
                {
                    if (value.ChoiceValue == "automatic")
                    {
                        var restored = new List<string>();
                        foreach (var fan in _changedFans)
                        {
                            if (!_journal!.HasUnrestoredMutation("fan-" + fan))
                            {
                                continue;
                            }

                            token.ThrowIfCancellationRequested();
                            dispatched = true;
                            await RestoreAsync("fan-" + fan, token).ConfigureAwait(false);
                            restored.Add(fan);
                        }

                        foreach (var fan in restored)
                        {
                            _changedFans.Remove(fan);
                            _observed.Forget("fan.duty", fan);
                            _observed.Forget(CapabilityIds.FanCurve, fan);
                        }

                        if (dispatched && _changedFans.Count > 0)
                        {
                            return CommandResults.Indeterminate(command, CapabilityReasonCode.PrerequisiteMissing,
                                "Only the captured fan tables were restored; another changed fan has an unknown original.");
                        }

                        // HHD disables its Linux hwmon override separately from changing thermal policy.
                        // No equivalent Windows reset is documented for Flow; a mode write alone cannot
                        // be claimed to release custom fan ownership when the original curves are unknown.
                        if (!dispatched)
                        {
                            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                                "The original fan tables were not readable; the Windows automatic reset protocol is not known.");
                        }
                    }
                    else
                    {
                        foreach (var instance in new[] { "cpu", "gpu" })
                        {
                            if (_observed.Value("fan.duty", instance)?.IntegerValue is not { } duty)
                            {
                                continue;
                            }

                            var curve = AsusAcpiProtocol.FlatFanCurve(duty);
                            token.ThrowIfCancellationRequested();
                            dispatched = true;
                            _acpi.WriteBuffer(instance == "gpu" ? AsusAcpiId.GpuFanCurve : AsusAcpiId.CpuFanCurve,
                                curve);
                        }

                        if (!dispatched)
                        {
                            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                                "Set a fan duty to enter native custom control.");
                        }
                    }
                }
                else if (command.CapabilityId == CapabilityIds.FanCurve)
                {
                    if (!AllyFanCapability.TryEncode(value.CurveValue, out var curve, out var error))
                    {
                        return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                    }

                    var id = command.InstanceId == "gpu" ? AsusAcpiId.GpuFanCurve : AsusAcpiId.CpuFanCurve;
                    await CaptureAsync("fan-" + command.InstanceId,
                        [command.InstanceId == "gpu" ? (ushort)2 : (ushort)1], () =>
                        {
                            var mode = OriginalMode();
                            if (mode is null)
                            {
                                return null;
                            }

                            var read = _acpi.ReadBuffer(id, AsusAcpiProtocol.CurveSelector(mode[0]));
                            return AsusAcpiProtocol.IsValidCurve(read) ? [mode[0], .. read] : null;
                        }, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _changedFans.Add(command.InstanceId!);
                    _acpi.WriteBuffer(id, curve);
                }
                else
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "Unknown ASUS firmware control.");
                }
            }

            _observed.Written(command.CapabilityId, value, command.InstanceId);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    private ValueTask CaptureFanAsync(string instance, AsusAcpiId id, CancellationToken token)
    {
        return CaptureAsync("fan-" + instance, [instance == "gpu" ? (ushort)2 : (ushort)1], () =>
        {
            var mode = OriginalMode();
            if (mode is null)
            {
                return null;
            }

            var curve = _acpi!.ReadBuffer(id, AsusAcpiProtocol.CurveSelector(mode[0]));
            return AsusAcpiProtocol.IsValidCurve(curve) ? [mode[0], .. curve] : null;
        }, token);
    }

    private byte[]? OriginalMode()
    {
        return _acpi is not null && AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(AsusAcpiId.PerformanceMode),
                                     out var mode)
                                 && mode is 0 or 1 or 2 or 4
            ? [(byte)mode]
            : null;
    }

    private async ValueTask CaptureAsync(string id, ushort[] addresses, Func<byte[]?> capture, CancellationToken token)
    {
        if (!_captureAttempted.Add(id))
        {
            return;
        }

        // The original is captured once solely for restoration; it is not a control observation.
        var captured = await _journal!.BeginAsync(id, context.Firmware, () =>
        {
            var original = capture();
            return original is null ? null : new GenericRecoveryState { Addresses = addresses, Values = original };
        }, token).ConfigureAwait(false);
        _unknownOriginal |= captured is null;
    }

    private async ValueTask RestoreAsync(string id, CancellationToken token)
    {
        if (_acpi is null || _journal?.PendingOriginalFor<GenericRecoveryState>(id) is not { } original)
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
                var current = _observed.Value(CapabilityIds.Scenario)?.ChoiceValue;
                _acpi.Write(AsusAcpiId.PerformanceMode, original.Values[0]);
                _acpi.WriteBuffer(id == "fan-gpu" ? AsusAcpiId.GpuFanCurve : AsusAcpiId.CpuFanCurve,
                    original.Values.AsSpan(1));
                if (current is not null)
                {
                    _acpi.Write(AsusAcpiId.PerformanceMode, AllyModels.ScenarioValue(current));
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

            return _unknownOriginal || _journal?.Entries<GenericRecoveryState>().Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The original ASUS temporary settings were not completely captured for restoration."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            _acpi?.Dispose();
            _acpi = null;
            _charge = null;
            _xg?.Dispose();
            _xg = null;
            _aura.Close();
            _observed.Clear();
        }
    }

    internal override ValueTask DisposeAsync()
    {
        _aura.Close();
        _xg?.Dispose();
        _xg = null;
        _acpi?.Dispose();
        _acpi = null;
        _charge = null;
        return ValueTask.CompletedTask;
    }


    private static CapabilityDescriptor Duty(string instance)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = "fan.duty", InstanceId = instance, Role = CapabilityRole.FanDuty,
            ValueKind = CapabilityValueKind.Integer, Display = new CapabilityDisplay { Key = DisplayKey.FanSpeed },
            Minimum = 0, Maximum = 100, Step = 1, Unit = CapabilityUnit.Percent, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Fan(string instance, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.FanCurve, InstanceId = instance, Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve,
            Display = CapabilityDescriptors.Label(label), Minimum = 0, Maximum = 100, Unit = CapabilityUnit.Percent,
            SupportsWrite = true,
            Persistence = instance == "xg" ? CapabilityPersistence.Unknown : CapabilityPersistence.Volatile
        };
    }
}
