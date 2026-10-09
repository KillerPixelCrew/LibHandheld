using System.Text.Json.Serialization;
using LibHandheld.Internal.Services;

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

internal sealed class ProfiledRecoveryJournal()
    : DeviceRecoveryJournal<ProfiledRecoveryState>(ProfiledRecoveryJson.Default.RecoveryDocument)
{
    internal static async ValueTask<ProfiledRecoveryJournal> Open(string directory, CancellationToken token)
    {
        var journal = new ProfiledRecoveryJournal();
        await journal.LoadAsync(directory, token).ConfigureAwait(false);
        return journal;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<ProfiledRecoveryState> entry)
    {
        var state = entry.OriginalState;
        var valid = entry.ServiceId switch
        {
            "power" => ValidPower(state.Sustained) && ValidPower(state.Slow) && ValidPower(state.Boost),
            "fan" or "fan-unlock" => state.Addresses.Length is > 0 and <= 8 && state.Values.Length ==
                                                                            state.Addresses.Length
                                                                            && state.Addresses.Distinct().Count() ==
                                                                            state.Addresses.Length,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException("The device recovery entry does not match its service.");
        }
    }

    private static bool ValidPower(double? value)
    {
        return value is > 0 and <= 250 && double.IsFinite(value.Value);
    }
}

[JsonSerializable(typeof(DeviceRecoveryDocument<ProfiledRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal partial class ProfiledRecoveryJson : JsonSerializerContext;
