using System.Text.Json;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Devices;

/// <summary>Settings this processor's firmware refused outright, remembered so they stay hidden from the start.</summary>
/// <remarks>
///     The record is bound to the processor and the firmware that refused. A BIOS update can add a command,
///     so a different binding forgets every entry. A record that cannot be read is treated as empty: the
///     setting then shows until the firmware refuses it again.
/// </remarks>
internal sealed class UnsupportedSettings
{
    internal const string FileName = "unsupported-settings.json";

    private readonly string _binding;
    private readonly SortedSet<string> _ids;
    private readonly string _path;

    private UnsupportedSettings(string path, string binding, SortedSet<string> ids)
    {
        _path = path;
        _binding = binding;
        _ids = ids;
    }

    /// <summary>Loads the record for one processor and firmware.</summary>
    /// <param name="directory">The device state directory.</param>
    /// <param name="binding">Processor and firmware identity; another value forgets the record.</param>
    /// <returns>The record, empty when absent, unreadable or bound to other firmware.</returns>
    internal static UnsupportedSettings Load(string directory, string binding)
    {
        var path = Path.Combine(directory, FileName);
        SortedSet<string> ids = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) is { } record
                && string.Equals(record.Binding, binding, StringComparison.Ordinal))
            {
                ids.UnionWith(record.Settings);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            DeviceTrace.Write(DeviceTraceLevel.Warn, "processor",
                $"The unsupported-settings record could not be read and is ignored: {exception.Message}");
        }

        return new UnsupportedSettings(path, binding, ids);
    }

    /// <summary>Whether the firmware has refused this setting.</summary>
    /// <param name="id">The capability identifier.</param>
    /// <returns>True when it is recorded as unsupported.</returns>
    internal bool Contains(string id)
    {
        return _ids.Contains(id);
    }

    /// <summary>Records a refused setting and writes the record.</summary>
    /// <param name="id">The capability identifier.</param>
    internal void Add(string id)
    {
        if (!_ids.Add(id))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Record(_binding, [.. _ids])));
            File.Move(temporary, _path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The setting stays hidden for this run; the next start learns it again from the firmware.
            DeviceTrace.Write(DeviceTraceLevel.Warn, "processor",
                $"The unsupported-settings record could not be saved: {exception.Message}");
        }
    }

    private sealed record Record(string Binding, string[] Settings);
}
