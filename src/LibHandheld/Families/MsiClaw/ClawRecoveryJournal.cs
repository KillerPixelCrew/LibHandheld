using System.Text.Json.Serialization;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

/// <summary>Keeps only the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Power and fans bind to the BIOS version and the controller mode to the MCU binding
///     (<see cref="ClawFirmwareIdentities" />). <see cref="ClawRecoveryRules.Decide" /> holds the start-time rule for
///     power
///     and fans; the controller entry re-reads the pad before it writes, so it is restored at every start.
/// </remarks>
internal static class ClawRecoveryRules
{
    internal static ClawReconciliationAction Decide(DeviceRecoveryEntry<ClawRecoveryState> entry, string? binding)
    {
        if (binding is null)
        {
            return ClawReconciliationAction.Wait;
        }

        return entry.FirmwareIdentity == binding ? ClawReconciliationAction.Restore : ClawReconciliationAction.Discard;
    }
}

internal enum ClawReconciliationAction
{
    /// <summary>Write the original once.</summary>
    Restore,

    /// <summary>The firmware cannot be read this cycle: write nothing and keep the entry pending.</summary>
    Wait,

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
