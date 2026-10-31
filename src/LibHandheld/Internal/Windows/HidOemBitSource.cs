using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

/// <summary>One OEM control that a vendor HID input report carries as one bit.</summary>
/// <param name="Id">The control id.</param>
/// <param name="Label">The printed name.</param>
/// <param name="Byte">The report byte holding the bit, counted from the report id.</param>
/// <param name="Mask">The bit within that byte.</param>
/// <param name="Button">The controller button the control maps to, or none.</param>
internal readonly record struct OemReportBit(string Id, string Label, int Byte, byte Mask, CanonicalButtons Button);

/// <summary>Publishes the edges of OEM controls that one vendor HID collection reports as bits.</summary>
/// <remarks>
///     Each accepted report is decoded against the table into one state word whose bit order is the
///     table order; a changed bit publishes its control's edge. The reader's end releases every held control.
/// </remarks>
internal sealed class HidOemBitSource : IProfiledOemSource
{
    private readonly IReadOnlyList<OemReportBit> _bits;
    private readonly Func<HidCollection?> _find;
    private readonly int _minimumLength;
    private readonly byte? _reportId;
    private readonly HidReportSource _source = new();
    private uint _held;
    private Action<string, CanonicalButtons, bool>? _publish;

    /// <summary>Creates a source that is not started yet.</summary>
    /// <param name="bits">The controls, at most 32, in state-bit order.</param>
    /// <param name="find">Selects the one attributed collection, or null when it is absent or ambiguous.</param>
    /// <param name="minimumLength">The shortest report that carries every bit.</param>
    /// <param name="reportId">The report id the bits belong to, or null when the collection has one report.</param>
    public HidOemBitSource(IReadOnlyList<OemReportBit> bits, Func<HidCollection?> find, int minimumLength,
        byte? reportId)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits.Count, 32, nameof(bits));
        foreach (var bit in bits)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bit.Byte, minimumLength, nameof(bits));
        }

        _bits = bits;
        _find = find;
        _minimumLength = minimumLength;
        _reportId = reportId;
    }

    public bool IsConnected => _source.IsConnected;

    public ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_find() is not { } collection)
        {
            return ValueTask.FromResult(false);
        }

        _publish = publish;
        return ValueTask.FromResult(_source.Start(HidDevices.OpenStream(collection), collection.InputLength,
            HandleReport, () => Emit(0), token));
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        await _source.StopAsync(token).ConfigureAwait(false);
        _publish = null;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    /// <summary>The OEM control descriptors of a bit table, all on the front.</summary>
    /// <param name="bits">The table.</param>
    /// <returns>One descriptor per bit, in table order.</returns>
    internal static IReadOnlyList<OemControlDescriptor> Controls(IReadOnlyList<OemReportBit> bits)
    {
        return bits.Select(bit =>
            CapabilityDescriptors.OemControl(bit.Id, bit.Label, OemControlPlacement.Front, button: bit.Button))
            .ToArray();
    }

    /// <summary>Decodes one report into the state word.</summary>
    /// <param name="report">The report, starting with its id byte.</param>
    /// <param name="state">The held controls, bit <c>n</c> for table entry <c>n</c>; zero when not accepted.</param>
    /// <returns>False for a report that is too short or carries another report id.</returns>
    internal bool TryDecode(ReadOnlySpan<byte> report, out uint state)
    {
        state = 0;
        if (report.Length < _minimumLength || (_reportId is { } id && report[0] != id))
        {
            return false;
        }

        for (var index = 0; index < _bits.Count; index++)
        {
            if ((report[_bits[index].Byte] & _bits[index].Mask) != 0)
            {
                state |= 1u << index;
            }
        }

        return true;
    }

    private void HandleReport(ReadOnlySpan<byte> report)
    {
        if (TryDecode(report, out var state))
        {
            Emit(state);
        }
    }

    private void Emit(uint current)
    {
        for (var index = 0; index < _bits.Count; index++)
        {
            var mask = 1u << index;
            if (((current ^ _held) & mask) != 0)
            {
                _publish?.Invoke(_bits[index].Id, _bits[index].Button, (current & mask) != 0);
            }
        }

        _held = current;
    }
}
