namespace LibHandheld.Contracts;

/// <summary>A handheld maker's own manager app and the Windows identities it starts and runs under.</summary>
/// <param name="Id">Stable identity, used by a consumer's logs and recovery records.</param>
/// <param name="Name">Product name as the maker ships it.</param>
/// <param name="Processes">Its process names, without extension.</param>
/// <param name="Services">Its Windows services, by service name.</param>
/// <param name="Tasks">Its scheduled tasks, by name in the root task folder.</param>
/// <remarks>
///     Facts only: the library neither detects nor changes these apps. The consumer decides whether, when
///     and how to turn them off and how to put them back.
/// </remarks>
public sealed record OemManagerApp(
    string Id,
    string Name,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Tasks)
{
    /// <summary>Exact MSIX package family names the app is installed as.</summary>
    public IReadOnlyList<string> PackageFamilies { get; init; } = [];

    /// <summary>
    ///     The hardware identity prefix of a keyboard interface that exists only to feed the app, or null.
    /// </summary>
    public string? DedicatedKeyboardDeviceId { get; init; }

    /// <summary>
    ///     The exact definition the app belongs to, so it is considered only on that model; null when its
    ///     processes, services and tasks name it on any machine.
    /// </summary>
    public string? DefinitionId { get; init; }
}
