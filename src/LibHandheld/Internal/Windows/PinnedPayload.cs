using System.Security.Cryptography;

namespace LibHandheld.Internal.Windows;

internal static class PinnedPayload
{
    internal static FileStream OpenHeld(string path, string digest)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Native payload {Path.GetFileName(path)} does not match its pinned digest.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
