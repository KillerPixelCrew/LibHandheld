using System.Text.Json.Serialization;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

/// <summary>Keeps only the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Power and fans bind to the BIOS version and the controller mode to the MCU binding
///     (<see cref="ClawFirmwareIdentities" />). <see cref="Decide" /> holds the start-time rule for power
///     and fans; the controller entry re-reads the pad before it writes, so it is restored at every start.
/// </remarks>
internal sealed class ClawRecoveryJournal()
    : DeviceRecoveryJournal<ClawRecoveryState>(ClawRecoveryJsonContext.Default.RecoveryDocument)
{
    public static async ValueTask<ClawRecoveryJournal> OpenAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var journal = new ClawRecoveryJournal();
        await journal.LoadAsync(stateDirectory, cancellationToken).ConfigureAwait(false);
        return journal;
    }

    /// <summary>What the start of a cycle does with an outstanding power or fan entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="binding">This start's binding, or null when the BIOS version or MSI_ACPI is unavailable.</param>
    /// <param name="legacyBinding">This start's binding in the form earlier builds wrote, or null.</param>
    /// <returns>
    ///     Restore only an eligible pending matching binding; otherwise wait, retain unresolved state, or discard a
    ///     changed binding.
    /// </returns>
    internal static ClawReconciliationAction Decide(
        DeviceRecoveryEntry<ClawRecoveryState> entry,
        string? binding,
        string? legacyBinding)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (ClawFirmwareIdentities.IsLegacy(entry.FirmwareIdentity))
        {
            // An earlier build bound to the EC and MSI_ACPI versions. Only a pending entry whose binding
            // this start reads again is restored, once; an unresolved one would be replayed, and no later
            // command could re-arm it under the new binding, so it is dropped with every other one.
            if (legacyBinding is null)
            {
                return ClawReconciliationAction.Wait;
            }

            return entry.Status is DeviceRecoveryStatus.Pending
                   && !ClawFirmwareIdentities.IsUnknownEc(entry.FirmwareIdentity)
                   && !ClawFirmwareIdentities.IsUnknownEc(legacyBinding)
                   && string.Equals(entry.FirmwareIdentity, legacyBinding, StringComparison.Ordinal)
                ? ClawReconciliationAction.Restore
                : ClawReconciliationAction.Discard;
        }

        if (binding is null)
        {
            // Nothing can be written or compared this cycle; the entry stays pending for one that can.
            return ClawReconciliationAction.Wait;
        }

        if (!string.Equals(entry.FirmwareIdentity, binding, StringComparison.Ordinal))
        {
            // A BIOS update rewrites the state the entry captured, so it is dropped rather than restored.
            return ClawReconciliationAction.Discard;
        }

        // A restore that already failed or did not verify is never written again automatically; the
        // service stays usable and the next explicit command re-arms it.
        return entry.Status is DeviceRecoveryStatus.Pending
            ? ClawReconciliationAction.Restore
            : ClawReconciliationAction.Keep;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<ClawRecoveryState> entry)
    {
        var expectedFirmware = entry.ServiceId switch
        {
            ServiceIds.Power or ServiceIds.Fans => ClawFirmwareIdentities.IsBios(entry.FirmwareIdentity)
                                                   || ClawFirmwareIdentities.IsLegacy(entry.FirmwareIdentity),
            ServiceIds.Controller => string.Equals(entry.FirmwareIdentity, ClawFirmwareIdentities.Mcu,
                StringComparison.Ordinal),
            _ => throw new InvalidDataException("A recovery entry names a non-restorable service.")
        };
        if (!expectedFirmware)
        {
            throw new InvalidDataException("A recovery entry has an unexpected firmware identity.");
        }

        var state = entry.OriginalState;
        var valid = entry.ServiceId switch
        {
            ServiceIds.Power => state.Kind is ClawRecoveryStateKind.Power
                                && state.SustainedWatts is >= byte.MinValue and <= byte.MaxValue
                                && state.BoostWatts is >= byte.MinValue and <= byte.MaxValue
                                && state.FastWatts is null or >= byte.MinValue and <= byte.MaxValue
                                && state.Scenario is not null,
            ServiceIds.Fans => state.Kind is ClawRecoveryStateKind.Fans
                               && state.LeftDuty.Length == 32
                               && state.LeftTemperature.Length == 32
                               && state.RightDuty.Length == 32
                               && state.RightTemperature.Length == 32
                               && state.CustomFlag is not null
                               && state.FullSpeedFlag is not null,
            _ => state.Kind is ClawRecoveryStateKind.ControllerMode
                 && state.ControllerMode is ClawControllerMode.XInput or ClawControllerMode.DirectInput
        };
        if (!valid)
        {
            throw new InvalidDataException($"Recovery state does not match service '{entry.ServiceId}'.");
        }
    }
}

internal enum ClawReconciliationAction
{
    /// <summary>Write the original once.</summary>
    Restore,

    /// <summary>The firmware cannot be read this cycle: write nothing and keep the entry pending.</summary>
    Wait,

    /// <summary>An earlier restore failed or did not verify: write nothing until a command re-arms it.</summary>
    Keep,

    /// <summary>The firmware changed under the entry: drop it without restoring.</summary>
    Discard
}

internal sealed record ClawRecoveryState
{
    public required ClawRecoveryStateKind Kind { get; init; }

    public int? SustainedWatts { get; init; }

    public int? BoostWatts { get; init; }

    public int? FastWatts { get; init; }

    public byte? Scenario { get; init; }

    public byte[] LeftDuty { get; init; } = [];

    public byte[] LeftTemperature { get; init; } = [];

    public byte[] RightDuty { get; init; } = [];

    public byte[] RightTemperature { get; init; } = [];

    public byte? CustomFlag { get; init; }

    public byte? FullSpeedFlag { get; init; }

    public ClawControllerMode? ControllerMode { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClawRecoveryStateKind>))]
internal enum ClawRecoveryStateKind
{
    Power,
    Fans,
    ControllerMode
}

internal static class ClawRecoveryValues
{
    public static ClawRecoveryState Power(PowerPair snapshot)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.Power,
            SustainedWatts = snapshot.SustainedWatts,
            BoostWatts = snapshot.BoostWatts,
            FastWatts = snapshot.FastWatts,
            Scenario = snapshot.Scenario
        };
    }

    public static bool TryPower(ClawRecoveryState? value, out PowerPair? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not ClawRecoveryStateKind.Power
            || value.SustainedWatts is not { } sustained
            || value.BoostWatts is not { } boost
            || value.Scenario is not { } scenario)
        {
            return false;
        }

        snapshot = new PowerPair(sustained, boost, scenario, value.FastWatts);
        return true;
    }

    public static ClawRecoveryState Fans(FanSnapshot snapshot)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.Fans,
            LeftDuty = [.. snapshot.Left.DutyBuffer],
            LeftTemperature = [.. snapshot.Left.TemperatureBuffer],
            RightDuty = [.. snapshot.Right.DutyBuffer],
            RightTemperature = [.. snapshot.Right.TemperatureBuffer],
            CustomFlag = snapshot.CustomFlag,
            FullSpeedFlag = snapshot.FullSpeedFlag
        };
    }

    public static bool TryFans(ClawRecoveryState? value, out FanSnapshot? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not ClawRecoveryStateKind.Fans
            || value.LeftDuty.Length != 32
            || value.LeftTemperature.Length != 32
            || value.RightDuty.Length != 32
            || value.RightTemperature.Length != 32
            || value.CustomFlag is not { } customFlag
            || value.FullSpeedFlag is not { } fullSpeedFlag)
        {
            return false;
        }

        snapshot = new FanSnapshot(
            new FanTable([.. value.LeftDuty], [.. value.LeftTemperature]),
            new FanTable([.. value.RightDuty], [.. value.RightTemperature]),
            customFlag,
            fullSpeedFlag);
        return true;
    }

    public static ClawRecoveryState ControllerMode(ClawControllerMode mode)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.ControllerMode,
            ControllerMode = mode
        };
    }

    public static bool TryControllerMode(ClawRecoveryState? value, out ClawControllerMode mode)
    {
        mode = value?.ControllerMode ?? ClawControllerMode.Offline;
        return value?.Kind is ClawRecoveryStateKind.ControllerMode
               && mode is ClawControllerMode.XInput or ClawControllerMode.DirectInput;
    }
}

// Unknown members are ignored: a record a different build of this package left behind must still
// load, or the services it covers would stay blocked with nothing in the product able to clear it.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<ClawRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal sealed partial class ClawRecoveryJsonContext : JsonSerializerContext;
