using System.Text.Json.Serialization;

namespace LibHandheld.Families.MsiClaw;

[JsonConverter(typeof(JsonStringEnumConverter<ClawRecoveryStateKind>))]
internal enum ClawRecoveryStateKind
{
    Power,
    Fans,
    ControllerMode
}

/// <summary>Keeps only the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Power and fans bind to the BIOS version (<see cref="ClawIdentityState.RecoveryBinding" />) and the
///     controller mode to <see cref="ClawServiceIds.McuFirmware" />. Charge limit and lighting are persistent
///     user choices and are never journalled. The journal persists this record by its full type name, so the
///     name never changes.
/// </remarks>
// Unknown members are ignored: a record a different build of this package left behind must still
// load, or the services it covers would stay blocked with nothing in the product able to clear it.
internal sealed record ClawRecoveryState
{
    /// <summary>The length of every raw fan-table package.</summary>
    private const int FanPackageLength = 32;

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

    public static ClawRecoveryState Controller(ClawControllerMode mode)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.ControllerMode,
            ControllerMode = mode
        };
    }

    /// <summary>The captured power original, or null when this is not a complete power record.</summary>
    /// <returns>The scenario and pair to write back, with the fast limit when it was read.</returns>
    public PowerPair? ToPower()
    {
        return Kind is ClawRecoveryStateKind.Power
               && SustainedWatts is { } sustained
               && BoostWatts is { } boost
               && Scenario is { } scenario
            ? new PowerPair(sustained, boost, scenario, FastWatts)
            : null;
    }

    /// <summary>The captured fan original, or null when this is not a complete fan record.</summary>
    /// <returns>Both raw tables and both flags, copied so a restore cannot change the record.</returns>
    public FanSnapshot? ToFans()
    {
        return Kind is ClawRecoveryStateKind.Fans
               && LeftDuty.Length == FanPackageLength
               && LeftTemperature.Length == FanPackageLength
               && RightDuty.Length == FanPackageLength
               && RightTemperature.Length == FanPackageLength
               && CustomFlag is { } customFlag
               && FullSpeedFlag is { } fullSpeedFlag
            ? new FanSnapshot(
                new FanTable([.. LeftDuty], [.. LeftTemperature]),
                new FanTable([.. RightDuty], [.. RightTemperature]),
                customFlag,
                fullSpeedFlag)
            : null;
    }

    /// <summary>The controller mode to put back, or null when this is not a writable controller record.</summary>
    /// <returns>XInput or DirectInput.</returns>
    public ClawControllerMode? ToControllerMode()
    {
        return Kind is ClawRecoveryStateKind.ControllerMode
               && ControllerMode is ClawControllerMode.XInput or ClawControllerMode.DirectInput
            ? ControllerMode
            : null;
    }
}
