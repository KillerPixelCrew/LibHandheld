using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Generic;

/// <summary>Loki uses the ACPI EC command interface, not the unrelated indexed SuperIO fan transport.</summary>
internal sealed class AynNativeService(ProfiledFeatureContext context, Func<IEmbeddedController>? open = null)
    : ProfiledFeatureService("ayn-controls")
{
    private bool _captureAttempted;
    private CapabilityValue? _color;
    private DateTimeOffset? _colorWrittenAt;
    private int? _duty;
    private IEmbeddedController? _ec;
    private DateTimeOffset? _fanWrittenAt;
    private DeviceRecoveryJournal? _journal;
    private string? _mode;
    private bool _unknownOriginal;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
    [
        new()
        {
            CapabilityId = CapabilityIds.FanMode, Role = CapabilityRole.FanMode, ValueKind = CapabilityValueKind.Choice,
            Display = CapabilityDescriptors.Label("Fan mode"), SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices =
            [
                new CapabilityChoice("automatic", CapabilityDescriptors.Label("Automatic")),
                new CapabilityChoice("custom", CapabilityDescriptors.Label("Custom")),
                new CapabilityChoice("full-speed", CapabilityDescriptors.Label("Full speed"))
            ]
        },
        new()
        {
            CapabilityId = "fan.duty", Role = CapabilityRole.FanDuty, ValueKind = CapabilityValueKind.Integer,
            Display = CapabilityDescriptors.Label("Fan speed"), Minimum = 0, Maximum = 100, Step = 1,
            SupportsWrite = true,
            Unit = CapabilityUnit.Percent, Persistence = CapabilityPersistence.Volatile
        },
        new()
        {
            CapabilityId = CapabilityIds.LightingColor, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = CapabilityDescriptors.Label("Lighting color"), SupportsWrite = true,
            Persistence = CapabilityPersistence.Unknown
        }
    ];

    internal override IReadOnlyList<CapabilityState> States => Array.AsReadOnly(Descriptors.Select(descriptor =>
    {
        var value = descriptor.CapabilityId switch
        {
            CapabilityIds.FanMode => _mode is null ? null : CapabilityValue.Choice(_mode),
            "fan.duty" => _duty is null ? null : CapabilityValue.Integer(_duty.Value), _ => _color
        };
        return new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId,
            Available = _ec is not null,
            ObservedValue = value,
            ObservedAt = value is null ? null
                : descriptor.CapabilityId == CapabilityIds.LightingColor ? _colorWrittenAt : _fanWrittenAt,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed
        };
    }).ToArray());

    public override bool Suspendable => true;

    internal override ValueTask DisposeAsync()
    {
        _ec?.Dispose();
        _ec = null;
        _duty = null;
        _mode = null;
        _color = null;
        _fanWrittenAt = null;
        _colorWrittenAt = null;
        return ValueTask.CompletedTask;
    }


    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ec = (open ?? PawnIoEmbeddedController.OpenAcpi)();
        _journal = context.Journal;
        await _journal.ReconcileAsync<GenericRecoveryState>("ayn-fan", context.Firmware,
            (_, ct) => RestoreAsync(ct), token).ConfigureAwait(false);

        _captureAttempted = false;
        return Set(DeviceServiceState.Owned);
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_ec is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "Loki ACPI EC access is unavailable.");
        }

        var dispatched = false;
        try
        {
            var value = command.RequestedValue!;
            if (command.CapabilityId is CapabilityIds.FanMode or "fan.duty")
            {
                if (!_captureAttempted)
                {
                    _captureAttempted = true;
                    var captured = await _journal!.BeginAsync("ayn-fan", firmware,
                            () => new GenericRecoveryState
                                { Addresses = [0x11, 0x10], Values = [_ec.ReadByte(0x11), _ec.ReadByte(0x10)] }, token)
                        .ConfigureAwait(false);
                    _unknownOriginal |= captured is null;
                }

                var mode = command.CapabilityId == CapabilityIds.FanMode ? value.ChoiceValue! : "custom";
                token.ThrowIfCancellationRequested();
                dispatched = true;
                _ec.Transaction(() =>
                {
                    _ec.WriteByte(0x10, mode == "automatic" ? (byte)1 : (byte)0);
                    if (mode == "full-speed" || command.CapabilityId == "fan.duty")
                    {
                        _ec.WriteByte(0x11, EncodeDuty(mode == "full-speed" ? 100 : value.IntegerValue!.Value));
                    }

                    return true;
                });
                _mode = mode;
                if (mode == "full-speed" || command.CapabilityId == "fan.duty")
                {
                    _duty = mode == "full-speed" ? 100 : value.IntegerValue!.Value;
                }
                else if (mode == "automatic")
                {
                    _duty = null;
                }

                _fanWrittenAt = DateTimeOffset.UtcNow;
            }
            else if (command.CapabilityId == CapabilityIds.LightingColor)
            {
                token.ThrowIfCancellationRequested();
                dispatched = true;
                _ec.Transaction(() =>
                {
                    var color = value.ColorValue!.Value;
                    // HC opens the LED command before its channels and commits it afterwards.
                    // Its optional completion read is not a gate for the supported write.
                    _ec.WriteByte(0xB3, 0xAA);
                    _ec.WriteByte(0xB0, (byte)(color >> 16));
                    _ec.WriteByte(0xB1, (byte)(color >> 8));
                    _ec.WriteByte(0xB2, (byte)color);
                    _ec.WriteByte(0xB3, 0xAA);
                    return true;
                });
                _color = value;
                _colorWrittenAt = DateTimeOffset.UtcNow;
            }
            else
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported, "Unknown Loki control.");
            }

            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    internal static byte EncodeDuty(int percentage)
    {
        return checked((byte)(Math.Clamp(percentage, 0, 100) * 128 / 100));
    }

    private async ValueTask RestoreAsync(CancellationToken token)
    {
        if (_ec is null || _journal?.PendingOriginalFor<GenericRecoveryState>("ayn-fan") is not { } original)
        {
            return;
        }

        try
        {
            _ec.Transaction(() =>
            {
                for (var index = 0; index < original.Addresses.Length; index++)
                {
                    _ec.WriteByte(original.Addresses[index], original.Values[index]);
                }

                return true;
            });
            await _journal.SetStatusAsync("ayn-fan", DeviceRecoveryStatus.RestoredUnverified, token)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.SetStatusAsync("ayn-fan", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            await RestoreAsync(token).ConfigureAwait(false);
            return _unknownOriginal || _journal?.Entries<GenericRecoveryState>().Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The original Loki fan settings were not captured for restoration."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            _ec?.Dispose();
            _ec = null;
            _duty = null;
            _mode = null;
            _fanWrittenAt = null;
        }
    }
}

internal sealed record GenericRecoveryState
{
    public ushort[] Addresses { get; init; } = [];
    public byte[] Values { get; init; } = [];
}
