using System.Text.Json.Serialization;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.LegionGo;

internal sealed record LegionRecoveryState
{
    public Dictionary<int, int> Features { get; init; } = [];
    public int? Mode { get; init; }
    public ushort[]? FanTable { get; init; }
    public ushort? EcFan { get; init; }
    public bool UnknownOriginal { get; init; }
}

internal sealed class LegionRecoveryJournal()
    : DeviceRecoveryJournal<LegionRecoveryState>(LegionRecoveryJsonContext.Default.Document)
{
    public static async ValueTask<LegionRecoveryJournal> OpenAsync(string directory, CancellationToken token)
    {
        var journal = new LegionRecoveryJournal();
        await journal.LoadAsync(directory, token).ConfigureAwait(false);
        return journal;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<LegionRecoveryState> entry)
    {
        if (entry.ServiceId is not ("power" or "mode" or "fans"))
        {
            throw new InvalidDataException("Unknown Lenovo recovery service.");
        }

        foreach (var feature in entry.OriginalState.Features)
        {
            if (feature.Key is not (LegionProtocol.LongPower or LegionProtocol.ShortPower or LegionProtocol.PeakPower
                    or LegionProtocol.CrossPower or LegionProtocol.FullFan) || feature.Value is < 0 or > 255)
            {
                throw new InvalidDataException("Invalid Lenovo feature recovery value.");
            }
        }

        if ((entry.OriginalState.FanTable is { } table && (table.Length != 10 || table.Any(value => value > 100))) ||
            (entry.OriginalState.Mode is { } mode && mode is not (1 or 2 or 3 or 255)) ||
            (entry.OriginalState.EcFan is { } rpm && rpm != 0 && rpm is < 1500 or > 5200))
        {
            throw new InvalidDataException("Invalid Lenovo fan or mode recovery value.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<LegionRecoveryState>), TypeInfoPropertyName = "Document")]
internal sealed partial class LegionRecoveryJsonContext : JsonSerializerContext;
