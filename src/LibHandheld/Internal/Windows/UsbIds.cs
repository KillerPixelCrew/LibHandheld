using System.Globalization;

namespace LibHandheld.Internal.Windows;

internal static class UsbIds
{
    internal static ushort? Parse(ReadOnlySpan<char> path, ReadOnlySpan<char> marker, int digits = 4)
    {
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && index + marker.Length + digits <= path.Length
                          && ushort.TryParse(path.Slice(index + marker.Length, digits), NumberStyles.HexNumber,
                              CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
