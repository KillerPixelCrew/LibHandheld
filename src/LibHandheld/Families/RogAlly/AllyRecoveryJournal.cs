using System.Text.Json.Serialization;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

[JsonConverter(typeof(JsonStringEnumConverter<AllyRecoveryStateKind>))]
internal enum AllyRecoveryStateKind
{
    Power,
    Fans,

    /// <summary>The MCU button tables. There is no readable original; restoring writes the defaults.</summary>
    ControllerConfiguration
}

/// <summary>Keeps the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Narrowed to what the Ally changes temporarily: the power limits and mode, the fan curves, and the
///     controller configuration. Charge limit and lighting are persistent user choices and are never
///     journalled or reverted.
/// </remarks>
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
