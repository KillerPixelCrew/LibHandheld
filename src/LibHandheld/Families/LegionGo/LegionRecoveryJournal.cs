namespace LibHandheld.Families.LegionGo;

internal sealed record LegionRecoveryState
{
    public Dictionary<int, int> Features { get; init; } = [];
    public int? Mode { get; init; }
    public ushort[]? FanTable { get; init; }
    public ushort? EcFan { get; init; }
    public bool UnknownOriginal { get; init; }
}
