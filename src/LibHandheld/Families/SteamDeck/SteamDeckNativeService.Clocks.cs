using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Families.SteamDeck;

internal sealed partial class SteamDeckNativeService
{
    private static readonly CapabilityDescriptor[] ClockDescriptors =
    [
        Clock("steamdeck.gpu-clock-min", null, "Minimum GPU clock", 200, 1900),
        Clock("steamdeck.gpu-clock-soft-min", null, "Soft minimum GPU clock", 200, 1900),
        Clock("steamdeck.gpu-clock-max", null, "Maximum GPU clock", 200, 1900),
        .. Enumerable.Range(0, 4).SelectMany(core => new[]
        {
            Clock("steamdeck.cpu-clock-min", $"core{core}", $"Core {core} minimum clock", 1400, 4000),
            Clock("steamdeck.cpu-clock-max", $"core{core}", $"Core {core} maximum clock", 1400, 4000)
        })
    ];

    private readonly Dictionary<(string Id, string? Instance), ClockObservation> _writtenClocks = [];
    private CapabilityReason? _clockReason;
    private VangoghPower? _clocks;

    private void AcquireClocks()
    {
        if (_clocks is not null)
        {
            return;
        }

        try
        {
            _clocks = openClocks is null ? VangoghPower.Open(context.Resources.InpOutPath) : openClocks();
            _clockReason = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _clockReason = Missing(exception.Message);
        }
    }

    private async ValueTask<CapabilityCommandResult> ExecuteClockAsync(CapabilityCommand command,
        CancellationToken token)
    {
        if (_clocks is null || _journal is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The exact Valve GPU resource range and SMU firmware must be acquired.");
        }

        var descriptor = ClockDescriptors.SingleOrDefault(item =>
            item.CapabilityId == command.CapabilityId && item.InstanceId == command.InstanceId);
        if (descriptor is null || command.RequestedValue is not { Kind: CapabilityValueKind.Integer } requested
                               || !CapabilityValueValidation.ValueMatches(requested, descriptor, out _))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "The clock value does not match this firmware's range.");
        }

        var otherId = command.CapabilityId.EndsWith("-max", StringComparison.Ordinal)
            ? command.CapabilityId[..^4] + "-min"
            : command.CapabilityId[..^4] + "-max";
        if (command.CapabilityId == "steamdeck.gpu-clock-soft-min")
        {
            otherId = "steamdeck.gpu-clock-max";
        }

        if (_writtenClocks.TryGetValue((otherId, command.InstanceId), out var paired)
            && (command.CapabilityId.EndsWith("-max", StringComparison.Ordinal)
                ? requested.IntegerValue < paired.Megahertz
                : requested.IntegerValue > paired.Megahertz))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "The clock minimum cannot exceed its maximum.");
        }

        if (command.CapabilityId == "steamdeck.gpu-clock-max"
            && _writtenClocks.TryGetValue(("steamdeck.gpu-clock-soft-min", null), out var softMinimum)
            && requested.IntegerValue < softMinimum.Megahertz)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "The GPU maximum cannot be below its soft minimum.");
        }

        var dispatched = false;
        try
        {
            // The firmware has no original clock getters, so there is no captured value to restore.

            token.ThrowIfCancellationRequested();
            var (message, value) =
                ClockCommand(command.CapabilityId, command.InstanceId, requested.IntegerValue!.Value);
            dispatched = true;
            _clocks.Exchange(message, value);
            _writtenClocks[(command.CapabilityId, command.InstanceId)] =
                new ClockObservation(requested.IntegerValue.Value, DateTimeOffset.UtcNow);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    internal static (uint Command, uint Argument) ClockCommand(string id, string? instance, int mhz)
    {
        if (id.StartsWith("steamdeck.gpu-clock-", StringComparison.Ordinal))
        {
            if (instance is not null || mhz is < 200 or > 1900)
            {
                throw new ArgumentOutOfRangeException(nameof(mhz));
            }

            var message = id switch
            {
                "steamdeck.gpu-clock-min" => 0x20u, "steamdeck.gpu-clock-soft-min" => 0x0Cu,
                "steamdeck.gpu-clock-max" => 0x1Fu, _ => throw new ArgumentOutOfRangeException(nameof(id))
            };
            return (message, (uint)mhz);
        }

        if (mhz is < 1400 or > 4000 || instance is not ("core0" or "core1" or "core2" or "core3"))
        {
            throw new ArgumentOutOfRangeException(nameof(instance));
        }

        var command = id switch
        {
            "steamdeck.cpu-clock-min" => 0x40u, "steamdeck.cpu-clock-max" => 0x41u,
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
        return (command, (uint)mhz | ((uint)(instance[^1] - '0') << 20));
    }

    private static CapabilityDescriptor Clock(string id, string? instance, string label, int min, int max)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = CapabilityRole.GenericRange,
            ValueKind = CapabilityValueKind.Integer,
            Display = CapabilityDescriptors.Label(label), Minimum = min, Maximum = max, Step = 1,
            Unit = CapabilityUnit.Megahertz,

            SupportsWrite = true, SupportsRead = false, Persistence = CapabilityPersistence.Volatile
        };
    }

    private IEnumerable<CapabilityState> ClockStates()
    {
        foreach (var descriptor in ClockDescriptors)
        {
            var value = _writtenClocks.TryGetValue((descriptor.CapabilityId, descriptor.InstanceId), out var written)
                ? CapabilityValue.Integer(written.Megahertz)
                : null;
            yield return new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
                Available = _clocks is not null, Reason = _clockReason, ObservedValue = value,
                Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = value is null ? null : written.At
            };
        }
    }

    private readonly record struct ClockObservation(int Megahertz, DateTimeOffset At);
}
