using System.Text.Json;
using LibHandheld.Families.Acer;

namespace LibHandheld.AtlasButtonTest;

/// <summary>
///     The rear-key experiment: XInput with the rear keys in Macro mode and each mapped, in RAM only, to a
///     keyboard key, so a held rear key is an ordinary input instead of the start of a system hotkey.
/// </summary>
/// <remarks>
///     Combination mode (1) diverts the whole pad into hotkey detection while M1 or M2 is held. Macro mode
///     (0) runs each key's own mapping instead. Predator Sense 1.0.1563 maps a key with index 1, flag 0x04
///     (MapOrMacroEn) and up to five key indices; 156 is F23 and 116 left Ctrl. The writes go to RAM
///     profile 1 through command 21, as Predator Sense's preview does, and never to ROM. The original mode
///     and both original mappings are saved before anything is written and restored afterwards; a session
///     that ended before restoring is restored at the start of the next.
/// </remarks>
internal sealed class RearKeyRemap : IAsyncDisposable
{
    /// <summary>Gamepad XInput (1) with the rear keys in Macro mode (0).</summary>
    private static readonly AcerControllerMode Macro = new(Gamepad: 1, RearKeys: 0);

    /// <summary>M1, rear right: F23.</summary>
    internal static readonly byte[] M1Mapping = [1, 0x04, 0, 156, 0, 0, 0, 0];

    /// <summary>M2, rear left: left Ctrl with F23.</summary>
    internal static readonly byte[] M2Mapping = [1, 0x04, 0, 116, 156, 0, 0, 0];

    private readonly AcerControllerTransport _transport;
    private readonly List<string> _notes;
    private Saved? _original;

    private RearKeyRemap(AcerControllerTransport transport, List<string> notes)
    {
        _transport = transport;
        _notes = notes;
    }

    private static string RecoveryFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibHandheld",
        "AtlasButtonTest", "recovery", "rear-key-remap.json");

    /// <summary>Restores a session left unrestored, saves the originals, then selects Macro and the two mappings.</summary>
    /// <param name="notes">Where each step's outcome is recorded for the report.</param>
    /// <returns>The applied remap, which must be disposed to restore the originals.</returns>
    internal static async Task<RearKeyRemap> ApplyAsync(List<string> notes)
    {
        var transport = AcerControllerTransport.Open()
                        ?? throw new IOException("The Atlas controller command lane is absent: "
                                                 + AcerControllerTransport.DescribePresent());
        var remap = new RearKeyRemap(transport, notes);
        try
        {
            if (File.Exists(RecoveryFile))
            {
                var pending = JsonSerializer.Deserialize<Saved>(await File.ReadAllTextAsync(RecoveryFile))
                              ?? throw new IOException("The saved rear-key recovery file is unreadable.");
                notes.Add("A previous session was not restored; restoring its saved originals first.");
                await remap.RestoreAsync(pending).ConfigureAwait(false);
            }

            var mode = await transport.ReadModeAsync(CancellationToken.None).ConfigureAwait(false);
            var m1 = await transport.ReadRearKeyAsync(1, CancellationToken.None).ConfigureAwait(false);
            var m2 = await transport.ReadRearKeyAsync(2, CancellationToken.None).ConfigureAwait(false);
            remap._original = new Saved(mode.Gamepad, mode.RearKeys, Convert.ToHexString(m1), Convert.ToHexString(m2));
            Directory.CreateDirectory(Path.GetDirectoryName(RecoveryFile)!);
            await File.WriteAllTextAsync(RecoveryFile, JsonSerializer.Serialize(remap._original)).ConfigureAwait(false);
            notes.Add($"Original: mode {mode.Gamepad}/{mode.RearKeys}, M1 {remap._original.M1}, M2 {remap._original.M2}.");

            if (mode != Macro)
            {
                await transport.WriteModeAsync(Macro, CancellationToken.None).ConfigureAwait(false);
            }

            await transport.WriteRearKeyAsync(1, M1Mapping, CancellationToken.None).ConfigureAwait(false);
            await transport.WriteRearKeyAsync(2, M2Mapping, CancellationToken.None).ConfigureAwait(false);
            var appliedMode = await transport.ReadModeAsync(CancellationToken.None).ConfigureAwait(false);
            var appliedM1 = await transport.ReadRearKeyAsync(1, CancellationToken.None).ConfigureAwait(false);
            var appliedM2 = await transport.ReadRearKeyAsync(2, CancellationToken.None).ConfigureAwait(false);
            notes.Add($"Applied: mode {appliedMode.Gamepad}/{appliedMode.RearKeys}, M1 {Convert.ToHexString(appliedM1)}, "
                      + $"M2 {Convert.ToHexString(appliedM2)}.");
            if (appliedMode != Macro || !appliedM1.SequenceEqual(M1Mapping) || !appliedM2.SequenceEqual(M2Mapping))
            {
                throw new IOException("The controller did not read back the Macro mode and both rear-key mappings.");
            }

            return remap;
        }
        catch
        {
            await remap.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Writes the original mappings and mode back and verifies them; the recovery file goes only once verified.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_original is { } original)
            {
                await RestoreAsync(original).ConfigureAwait(false);
            }
        }
        finally
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RestoreAsync(Saved original)
    {
        var m1 = Convert.FromHexString(original.M1);
        var m2 = Convert.FromHexString(original.M2);
        var mode = new AcerControllerMode(original.Gamepad, original.RearKeys);
        await _transport.WriteRearKeyAsync(1, m1, CancellationToken.None).ConfigureAwait(false);
        await _transport.WriteRearKeyAsync(2, m2, CancellationToken.None).ConfigureAwait(false);
        if (await _transport.ReadModeAsync(CancellationToken.None).ConfigureAwait(false) != mode)
        {
            await _transport.WriteModeAsync(mode, CancellationToken.None).ConfigureAwait(false);
        }

        var restoredMode = await _transport.ReadModeAsync(CancellationToken.None).ConfigureAwait(false);
        var restoredM1 = await _transport.ReadRearKeyAsync(1, CancellationToken.None).ConfigureAwait(false);
        var restoredM2 = await _transport.ReadRearKeyAsync(2, CancellationToken.None).ConfigureAwait(false);
        if (restoredMode != mode || !restoredM1.SequenceEqual(m1) || !restoredM2.SequenceEqual(m2))
        {
            _notes.Add($"RESTORE NOT VERIFIED: mode {restoredMode.Gamepad}/{restoredMode.RearKeys}, "
                       + $"M1 {Convert.ToHexString(restoredM1)}, M2 {Convert.ToHexString(restoredM2)}; "
                       + $"the originals stay in {RecoveryFile}.");
            throw new IOException("The original rear-key mappings and mode were not read back.");
        }

        File.Delete(RecoveryFile);
        _original = null;
        _notes.Add($"Restored and verified: mode {mode.Gamepad}/{mode.RearKeys}, M1 {original.M1}, M2 {original.M2}.");
    }

    /// <summary>The originals, as written to the recovery file before any change.</summary>
    internal sealed record Saved(byte Gamepad, byte RearKeys, string M1, string M2);
}
