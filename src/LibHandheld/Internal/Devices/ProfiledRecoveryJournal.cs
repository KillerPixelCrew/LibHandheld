namespace LibHandheld.Internal.Devices;

internal sealed record ProfiledRecoveryState
{
    public double? Sustained { get; init; }
    public double? Slow { get; init; }
    public double? Boost { get; init; }
    public ushort[] Addresses { get; init; } = [];
    public byte[] Values { get; init; } = [];
    public bool HasExactOriginal { get; init; } = true;
}
