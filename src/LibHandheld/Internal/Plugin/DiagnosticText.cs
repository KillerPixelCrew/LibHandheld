using System.ComponentModel;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Internal.Plugin;

/// <summary>Turns a caught exception into one line a capability reason or trace can carry.</summary>
internal static class DiagnosticText
{
    /// <summary>Formats what failed with the exception's type, message and any Win32 error code.</summary>
    /// <param name="context">What was being attempted.</param>
    /// <param name="exception">The exception that ended it.</param>
    /// <returns>
    ///     A single line in which every character <see cref="PlainText.IsUnsafe" /> rejects is a space.
    /// </returns>
    public static string FromException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var code = exception is Win32Exception win32 ? $" [error {win32.NativeErrorCode}]" : string.Empty;
        var message = $"{context} ({exception.GetType().Name}): {exception.Message}{code}";
        var plain = new char[message.Length];
        for (var index = 0; index < plain.Length; index++)
        {
            var character = message[index];
            plain[index] = PlainText.IsUnsafe(character) ? ' ' : character;
        }

        return new string(plain);
    }
}
