namespace LibHandheld.Families.SteamDeck;

internal sealed record SteamDeckOriginal
{
    public byte[] Registers { get; init; } = [];
    public ushort?[] Values { get; init; } = [];
    public uint[] Addresses { get; init; } = [];
    public uint[] Memory { get; init; } = [];
    public byte? FanMode { get; init; }
    public string[] ClockIds { get; init; } = [];
}
