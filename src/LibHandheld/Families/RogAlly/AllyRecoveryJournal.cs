// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.RogAlly;

/// <summary>Keeps the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Narrowed to what the Ally changes temporarily: the power limits and mode, the fan curves, and the
///     controller configuration. Charge limit and lighting are persistent user choices and are never
///     journalled or reverted.
/// </remarks>
internal sealed class AllyRecoveryJournal()
    : DeviceRecoveryJournal<AllyRecoveryState>(AllyRecoveryJsonContext.Default.RecoveryDocument)
{
    public static async ValueTask<AllyRecoveryJournal> OpenAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var journal = new AllyRecoveryJournal();
        await journal.LoadAsync(stateDirectory, cancellationToken).ConfigureAwait(false);
        return journal;
    }

    /// <summary>Whether a service already holds a pending original captured under this BIOS.</summary>
    /// <remarks>
    ///     Then the command writes without reading again: the first original stays the restore point. An
    ///     entry from another BIOS is replaced by the next capture, as <c>BeginAsync</c> does.
    /// </remarks>
    /// <param name="serviceId">Service whose original state would otherwise be recaptured.</param>
    /// <param name="firmwareIdentity">Current BIOS or MCU binding, compared ordinally.</param>
    /// <returns>True only for a pending original under the same firmware binding.</returns>
    public bool HoldsOriginal(string serviceId, string firmwareIdentity)
    {
        return EntryFor(serviceId) is { Status: DeviceRecoveryStatus.Pending } entry
               && string.Equals(entry.FirmwareIdentity, firmwareIdentity, StringComparison.Ordinal);
    }

    /// <summary>What the start of a cycle does with an outstanding entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="currentFirmwareIdentity">
    ///     This start's binding, or null when the transport the restore needs is not available yet.
    /// </param>
    /// <remarks>
    ///     A restore either dispatched or failed to dispatch; nothing is read back (D9). There is no
    ///     unresolved entry to keep: a failed restore stays pending and this start writes it once.
    /// </remarks>
    /// <returns>Wait when the transport binding is unavailable, Restore when it matches, or Discard when it changed.</returns>
    internal static AllyReconciliationAction Decide(
        DeviceRecoveryEntry<AllyRecoveryState> entry,
        string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (currentFirmwareIdentity is null)
        {
            return AllyReconciliationAction.Wait;
        }

        // A BIOS update changes the ACPI methods the captured state was written through, so the entry is
        // dropped rather than restored, as the Claw drops one after a firmware change.
        return string.Equals(entry.FirmwareIdentity, currentFirmwareIdentity, StringComparison.Ordinal)
            ? AllyReconciliationAction.Restore
            : AllyReconciliationAction.Discard;
    }

    /// <summary>Whether a captured limit is one the recovery record can hold.</summary>
    /// <param name="value">Captured wattage, or null for an unavailable reading.</param>
    /// <returns>True for 1-80 W; zero, null and values outside that recovery envelope are invalid.</returns>
    internal static bool IsValidWatts(int? value)
    {
        return value is >= 1 and <= 80;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<AllyRecoveryState> entry)
    {
        var state = entry.OriginalState;
        var valid = entry.ServiceId switch
        {
            AllyServiceIds.Power => state.Kind is AllyRecoveryStateKind.Power
                                    && state.Mode is null or >= 0 and <= 2
                                    && IsValidWatts(state.Sustained) && IsValidWatts(state.Slow)
                                    && IsValidWatts(state.Fast),
            AllyServiceIds.Fans => state.Kind is AllyRecoveryStateKind.Fans
                                   && AsusAcpiProtocol.IsValidCurve(state.CpuCurve)
                                   && AsusAcpiProtocol.IsValidCurve(state.GpuCurve)
                                   && (state.MidCurve.Length == 0 || AsusAcpiProtocol.IsValidCurve(state.MidCurve)),
            AllyServiceIds.Controller => state.Kind is AllyRecoveryStateKind.ControllerConfiguration,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException($"Recovery state does not match service '{entry.ServiceId}'.");
        }
    }
}

internal enum AllyReconciliationAction
{
    /// <summary>Write the original once.</summary>
    Restore,

    /// <summary>The transport is not available yet: write nothing and keep the entry pending.</summary>
    Wait,

    /// <summary>The BIOS changed under the entry: drop it without restoring.</summary>
    Discard
}

[JsonConverter(typeof(JsonStringEnumConverter<AllyRecoveryStateKind>))]
internal enum AllyRecoveryStateKind
{
    Power,
    Fans,

    /// <summary>The MCU button tables. There is no readable original; restoring writes the defaults.</summary>
    ControllerConfiguration
}

internal sealed record AllyRecoveryState
{
    public required AllyRecoveryStateKind Kind { get; init; }

    public int? Sustained { get; init; }

    public int? Slow { get; init; }

    public int? Fast { get; init; }

    public int? Mode { get; init; }

    public byte[] CpuCurve { get; init; } = [];

    public byte[] GpuCurve { get; init; } = [];

    public byte[] MidCurve { get; init; } = [];

    public static AllyRecoveryState Power(AllyPowerState state)
    {
        return new AllyRecoveryState
        {
            Kind = AllyRecoveryStateKind.Power,
            Sustained = state.Sustained,
            Slow = state.Slow,
            Fast = state.Fast,
            Mode = state.Mode
        };
    }

    public static AllyRecoveryState Fans(AllyFanSnapshot snapshot)
    {
        return new AllyRecoveryState
        {
            Kind = AllyRecoveryStateKind.Fans,
            CpuCurve = [.. snapshot.Cpu ?? []],
            GpuCurve = [.. snapshot.Gpu ?? []],
            MidCurve = [.. snapshot.Mid ?? []],
            Mode = snapshot.Mode
        };
    }

    public static AllyRecoveryState Controller()
    {
        return new AllyRecoveryState { Kind = AllyRecoveryStateKind.ControllerConfiguration };
    }

    public AllyPowerState? ToPower()
    {
        return Kind is AllyRecoveryStateKind.Power ? new AllyPowerState(Sustained, Slow, Fast, Mode) : null;
    }

    public AllyFanSnapshot? ToFans()
    {
        return Kind is AllyRecoveryStateKind.Fans
            ? new AllyFanSnapshot([.. CpuCurve], [.. GpuCurve], MidCurve.Length == 0 ? null : [.. MidCurve], Mode)
            : null;
    }
}

// Unknown members are ignored: a record a different build of this package left behind must still
// load, or the services it covers would stay blocked with nothing in the product able to clear it.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<AllyRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal sealed partial class AllyRecoveryJsonContext : JsonSerializerContext;
