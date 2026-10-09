using System.Text.Json.Serialization;

namespace LibHandheld.Contracts;

/// <summary>How WSGM carries a remembered capability value from one game to the next.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityProfileScope>))]
public enum CapabilityProfileScope
{
    /// <summary>
    ///     WSGM resolves the running game's value and writes it when the game changes. Every value
    ///     published before version 11 behaves this way.
    /// </summary>
    Switched,

    /// <summary>
    ///     One machine-wide value. A change is always saved to Global and never offered per game,
    ///     typically because the value holds only after a restart.
    /// </summary>
    GlobalOnly,

    /// <summary>
    ///     The publisher's driver keeps per-application values itself. WSGM writes the Global value
    ///     through commands and hands every game's overrides, with its executables, to the publisher,
    ///     which stores them natively so the driver applies them when the game starts.
    /// </summary>
    NativePerApplication
}

/// <summary>When a written capability value takes effect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityApplyTiming>))]
public enum CapabilityApplyTiming
{
    /// <summary>At once, including inside a running game.</summary>
    Immediate,

    /// <summary>The next time an application starts; a running game keeps what it started with.</summary>
    NextApplicationStart,

    /// <summary>After Windows restarts.</summary>
    SystemRestart
}
