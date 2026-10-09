using System.Text.Json.Serialization;
using LibHandheld.Internal.Services;

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

internal sealed class SteamDeckRecovery : DeviceRecoveryJournal<SteamDeckOriginal>
{
    private SteamDeckRecovery() : base(SteamDeckRecoveryJson.Default.Document)
    {
    }

    internal static async ValueTask<SteamDeckRecovery> Open(string directory, CancellationToken token)
    {
        var journal = new SteamDeckRecovery();
        await journal.LoadAsync(Path.Combine(directory, "steamdeck-native"), token).ConfigureAwait(false);
        return journal;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<SteamDeckOriginal> entry)
    {
        var state = entry.OriginalState;
        var valid = entry.ServiceId switch
        {
            "neptune" => state.Registers.SequenceEqual(new byte[] { 9, 71, 48 }) && state.Values.Length == 3,
            "deck-fan" => state.Addresses.SequenceEqual(new[] { 0xFE700B92, 0xFE700B95, 0xFE700B97 }) &&
                          state.Memory.Length == 3,
            "deck-charge" => state.Addresses.SequenceEqual(new[] { 0xFE700B9F }) && state.Memory.Length == 1
                && (state.Memory[0] <= 100 || state.Memory[0] == uint.MaxValue),
            "deck-clocks" => state.ClockIds.Length > 0 && state.ClockIds.Length <= 11
                                                       && state.ClockIds.All(id =>
                                                           id.StartsWith("steamdeck.gpu-clock-",
                                                               StringComparison.Ordinal)
                                                           || id.StartsWith("steamdeck.cpu-clock-",
                                                               StringComparison.Ordinal)),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException("The Steam Deck recovery entry does not match its native service.");
        }
    }
}

[JsonSerializable(typeof(DeviceRecoveryDocument<SteamDeckOriginal>), TypeInfoPropertyName = "Document")]
internal partial class SteamDeckRecoveryJson : JsonSerializerContext;
