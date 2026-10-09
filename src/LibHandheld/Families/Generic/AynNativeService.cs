using System.Text.Json.Serialization;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Generic;

/// <summary>Loki uses the ACPI EC command interface, not the unrelated indexed SuperIO fan transport.</summary>
internal sealed class AynNativeService(ProfiledFeatureContext context) : ProfiledFeatureService("ayn-controls")
{
    private CapabilityValue? _color;
    private int? _duty;
    private IEmbeddedController? _ec;
    private GenericRecoveryJournal? _journal;
    private string? _mode;
    private bool _unknownOriginal;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
    [
        new()
        {
            CapabilityId = CapabilityIds.FanMode, Role = CapabilityRole.FanMode, ValueKind = CapabilityValueKind.Choice,
            Display = Label("Fan mode"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices =
            [
                new CapabilityChoice("automatic", Label("Automatic")), new CapabilityChoice("custom", Label("Custom")),
                new CapabilityChoice("full-speed", Label("Full speed"))
            ]
        },
        new()
        {
            CapabilityId = "fan.duty", Role = CapabilityRole.FanDuty, ValueKind = CapabilityValueKind.Integer,
            Display = Label("Fan speed"), Minimum = 0, Maximum = 100, Step = 1, SupportsRead = true,
            SupportsWrite = true,
            Unit = CapabilityUnit.Percent, Persistence = CapabilityPersistence.Volatile
        },
        new()
        {
            CapabilityId = CapabilityIds.LightingColor, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = Label("Lighting color"), SupportsWrite = true, Persistence = CapabilityPersistence.Unknown
        }
    ];

    internal override IReadOnlyList<CapabilityState> States => Array.AsReadOnly(Descriptors.Select(descriptor =>
        new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, Available = _ec is not null,
            ObservedValue = descriptor.CapabilityId switch
            {
                CapabilityIds.FanMode => _mode is null ? null : CapabilityValue.Choice(_mode),
                "fan.duty" => _duty is null ? null : CapabilityValue.Integer(_duty.Value), _ => _color
            },
            Quality = HardwareStateQuality.Observed, DescriptorGeneration = 0, CycleGeneration = 0
        }).ToArray());

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ec = PawnIoEmbeddedController.OpenAcpi();
        _journal = await GenericRecoveryJournal.Open(Path.Combine(context.StateDirectory, "ayn-controls"), token)
            .ConfigureAwait(false);
        if (_journal.EntryFor("fan") is { } entry)
        {
            if (entry.FirmwareIdentity != context.Firmware)
            {
                await _journal.SetStatusAsync("fan", DeviceRecoveryStatus.RestoredVerified, token)
                    .ConfigureAwait(false);
            }
            else if (entry.Status == DeviceRecoveryStatus.Pending)
            {
                await RestoreAsync(token).ConfigureAwait(false);
            }
        }

        await RefreshAsync(token).ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ec is null)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var raw = _ec.ReadByte(0x11);
            if (raw <= 128)
            {
                _duty = (int)Math.Round(raw * 100d / 128);
            }

            _mode = _ec.ReadByte(0x10) == 1 ? "automatic" : _duty == 100 ? "full-speed" : "custom";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        return ValueTask.CompletedTask;
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
                if (_journal!.EntryFor("fan") is null)
                {
                    GenericRecoveryState? original = null;
                    try
                    {
                        original = new GenericRecoveryState
                            { Addresses = [0x11, 0x10], Values = [_ec.ReadByte(0x11), _ec.ReadByte(0x10)] };
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                    }

                    if (original is not null)
                    {
                        await _journal.BeginAsync("fan", firmware, original, token).ConfigureAwait(false);
                    }
                    else
                    {
                        _unknownOriginal = true;
                    }
                }
                else if (_journal.EntryFor("fan") is { Status: not DeviceRecoveryStatus.Pending } prior)
                {
                    await _journal.BeginAsync("fan", firmware, prior.OriginalState, token).ConfigureAwait(false);
                }

                var mode = command.CapabilityId == CapabilityIds.FanMode ? value.ChoiceValue! : "custom";
                token.ThrowIfCancellationRequested();
                dispatched = true;
                _ec.Transaction(() =>
                {
                    _ec.WriteByte(0x10, mode == "automatic" ? (byte)1 : (byte)0);
                    if (mode == "full-speed" || command.CapabilityId == "fan.duty")
                    {
                        _duty = mode == "full-speed" ? 100 : value.IntegerValue!.Value;
                        _ec.WriteByte(0x11, EncodeDuty(_duty.Value));
                    }

                    return true;
                });
                _mode = mode;
            }
            else if (command.CapabilityId == CapabilityIds.LightingColor)
            {
                token.ThrowIfCancellationRequested();
                dispatched = true;
                _ec.Transaction(() =>
                {
                    var color = value.ColorValue!.Value;
                    _ec.WriteByte(0xB0, (byte)(color >> 16));
                    _ec.WriteByte(0xB1, (byte)(color >> 8));
                    _ec.WriteByte(0xB2, (byte)color);
                    _ec.WriteByte(0xB3, 0xAA);
                    return true;
                });
                _color = value;
            }
            else
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported, "Unknown Loki control.");
            }

            return CommandResults.Unverified(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message,
                    RollbackResult.NotRequired)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    internal static byte EncodeDuty(int percentage)
    {
        return checked((byte)(Math.Clamp(percentage, 0, 100) * 128 / 100));
    }

    private async ValueTask RestoreAsync(CancellationToken token)
    {
        if (_ec is null || _journal?.PendingOriginalFor("fan") is not { } original)
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
            await _journal.SetStatusAsync("fan", DeviceRecoveryStatus.RestoredUnverified, token).ConfigureAwait(false);
        }
        catch
        {
            await _journal.SetStatusAsync("fan", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
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
            return _unknownOriginal || _journal?.OutstandingEntries.Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("Loki fan restoration has no independent confirmation."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            _ec?.Dispose();
            _ec = null;
            _duty = null;
            _mode = null;
        }
    }

    private static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = text };
    }
}

internal sealed record GenericRecoveryState
{
    public ushort[] Addresses { get; init; } = [];
    public byte[] Values { get; init; } = [];
}

internal sealed class GenericRecoveryJournal()
    : DeviceRecoveryJournal<GenericRecoveryState>(GenericRecoveryJson.Default.RecoveryDocument)
{
    internal static async ValueTask<GenericRecoveryJournal> Open(string directory, CancellationToken token)
    {
        var journal = new GenericRecoveryJournal();
        await journal.LoadAsync(directory, token).ConfigureAwait(false);
        return journal;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<GenericRecoveryState> entry)
    {
        if (entry.ServiceId == "fan" ? entry.OriginalState.Addresses.Length != entry.OriginalState.Values.Length
                                       || entry.OriginalState.Addresses.Length != 2 ||
                                       entry.OriginalState.Addresses[0] != 0x11 ||
                                       entry.OriginalState.Addresses[1] != 0x10
            : entry.ServiceId == "mode" ? entry.OriginalState.Addresses.Length != 1 ||
                                          entry.OriginalState.Values.Length != 1 || entry.OriginalState.Values[0] > 2
            : entry.ServiceId is "fan-cpu" or "fan-gpu" ? entry.OriginalState.Addresses.Length != 1 ||
                                                          entry.OriginalState.Values.Length != 17 ||
                                                          entry.OriginalState.Values[0] > 2
            : true)
        {
            throw new InvalidDataException("The native restoration register set is invalid.");
        }
    }
}

[JsonSerializable(typeof(DeviceRecoveryDocument<GenericRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal partial class GenericRecoveryJson : JsonSerializerContext;
