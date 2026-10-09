namespace LibHandheld.Internal.Plugin;

/// <summary>Severity of a plugin diagnostic.</summary>
internal enum DeviceTraceLevel
{
    /// <summary>A normal decision, observation, or state change.</summary>
    Info,

    /// <summary>A degraded, refused, or fallback path.</summary>
    Warn,

    /// <summary>A failure the plugin could not handle.</summary>
    Error,

    /// <summary>
    ///     Detail worth recording only while investigating a specific problem, suppressed by default.
    /// </summary>
    /// <remarks>
    ///     Declared last so the numeric values of the levels that existed before it do not move. Order
    ///     here is declaration order, not severity: the host maps each level explicitly.
    /// </remarks>
    Debug
}

/// <summary>
///     Ambient diagnostic sink for the active device plugin.
/// </summary>
/// <remarks>
///     This is deliberately shaped like WSGM's own <c>Log</c>, static and no-op until installed, and
///     for the same reason: instrumentation that costs plumbing does not get written. Tests may install
///     a sink or leave it unset. Sample loops must not trace because controller input runs at ~125 Hz.
/// </remarks>
internal static class DeviceTrace
{
    private static volatile IDeviceSink? _sink;

    /// <summary>Routes subsequent trace calls to a host adapter.</summary>
    /// <param name="sink">The adapter to write through, or null to silence tracing.</param>
    /// <remarks>
    ///     Called by the plugin once it has its host adapter, normally as the first statement of
    ///     <c>StartAsync</c>. Installing it early is the point: the failures worth tracing are the ones
    ///     that happen during startup.
    /// </remarks>
    public static void Install(IDeviceSink? sink)
    {
        _sink = sink;
    }

    /// <summary>Records a decision, observation, or state change on a normal path.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    public static void Info(string scope, string message)
    {
        Write(DeviceTraceLevel.Info, scope, message);
    }

    /// <summary>Records something that degraded, was refused, or fell back.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    public static void Warn(string scope, string message)
    {
        Write(DeviceTraceLevel.Warn, scope, message);
    }

    /// <summary>Records a failure the plugin could not handle.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    public static void Error(string scope, string message)
    {
        Write(DeviceTraceLevel.Error, scope, message);
    }

    /// <summary>Records detail that only matters while investigating a specific problem.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="message">The line.</param>
    /// <remarks>
    ///     The host suppresses this level unless diagnostics are raised, so it is the right home for
    ///     values that would drown the default log. It is not a licence to trace per sample: a
    ///     suppressed line still costs the call and the string that built it, and raising diagnostics
    ///     must not turn the log into the thing this level exists to prevent.
    /// </remarks>
    public static void Debug(string scope, string message)
    {
        Write(DeviceTraceLevel.Debug, scope, message);
    }

    /// <summary>Records a polled state, writing only when it differs from that key's last line.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="key">Stable identity of the thing observed, unique within <paramref name="scope" />.</param>
    /// <param name="message">The current state, written verbatim when it changed.</param>
    /// <param name="level">Level for the line when it is written.</param>
    /// <remarks>
    ///     A plugin polls hardware, and a poll loop that traces every pass is how a log stops being
    ///     readable: one measured device session produced 7,619 motion lines, 40% of everything
    ///     recorded, from two messages a 125 Hz reader kept re-stating either side of a threshold.
    ///     Repeats under a key are counted rather than dropped, so the next line that does change still
    ///     shows the poll kept running and for how long.
    /// </remarks>
    public static void Change(
        string scope,
        string key,
        string message,
        DeviceTraceLevel level = DeviceTraceLevel.Info)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var sink = _sink;
        if (sink is null || string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            sink.TraceChange(level, scope, key, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Same contract as Write: a trace never fails the operation it was describing.
        }
    }

    /// <summary>Records a caught exception with its type and message.</summary>
    /// <param name="scope">Subsystem producing the line, used as the log prefix.</param>
    /// <param name="context">What was being attempted.</param>
    /// <param name="ex">The exception that ended it.</param>
    /// <remarks>
    ///     A <c>catch</c> that sets a flag and moves on is the single most common way this codebase
    ///     lost a diagnosis: the WMI provider probe turned every failure into
    ///     <c>providerAvailable = false</c>, so a permissions problem, a missing instance and a
    ///     malformed response all reached the user as the same partially-available device. One call
    ///     here at the top of such a block is the difference.
    /// </remarks>
    public static void Failure(string scope, string context, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        Write(DeviceTraceLevel.Warn, scope, DiagnosticText.FromException(context, ex));
    }

    private static void Write(DeviceTraceLevel level, string scope, string message)
    {
        var sink = _sink;
        if (sink is null || string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            sink.Trace(level, scope, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A trace must never be able to fail the operation it was describing, and the sink is
            // an interface a plugin's own test double can implement badly.
        }
    }
}
