// SPDX-License-Identifier: MIT

namespace LibHandheld.Testing;

/// <summary>A uniquely named directory under the system temp folder, deleted on dispose.</summary>
/// <remarks>Linked into several test projects, so it uses no test-framework API.</remarks>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "wsgm-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception exception) when (
                attempt < 4
                && exception is IOException or UnauthorizedAccessException)
            {
                // Collectible plugin load contexts release their mapped package files only after
                // collection. Test cleanup waits for that documented unload boundary.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(20);
            }
        }
    }

    public string GetPath(params string[] segments)
    {
        return segments.Aggregate(Root, Path.Combine);
    }
}
