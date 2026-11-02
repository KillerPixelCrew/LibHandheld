namespace LibHandheld.Internal.Input;

/// <summary>Keeps a motion sensor session open only while its service owns motion and a consumer wants it.</summary>
/// <remarks>
///     A Windows sensor pair at its fastest interval wakes the processor for every reading, so an idle
///     device with nobody reading motion must not stream. Ownership comes from the service lifecycle,
///     demand from the host. <see cref="Start" /> opens the session once even without demand, so the
///     service can still report a missing sensor, and closes it again straight away. Opens and closes are
///     serialized and always converge on the latest wanted state.
/// </remarks>
/// <param name="open">Opens a session, or returns null when no sensor is available.</param>
internal sealed class MotionDemandSession(Func<IDisposable?> open)
{
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly Lock _gate = new();
    private bool _owned;
    private IDisposable? _session;
    private bool _wanted = true;

    /// <summary>Whether a session is open now.</summary>
    internal bool Streaming
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>Takes ownership and proves a sensor exists.</summary>
    /// <returns>False when no sensor session could be opened.</returns>
    internal bool Start()
    {
        _apply.Wait();
        try
        {
            lock (_gate)
            {
                if (_owned)
                {
                    return true;
                }
            }

            var session = Current() ?? open();
            if (session is null)
            {
                return false;
            }

            bool keep;
            lock (_gate)
            {
                _owned = true;
                keep = _wanted;
                _session = keep ? session : null;
            }

            if (!keep)
            {
                session.Dispose();
            }

            return true;
        }
        finally
        {
            _apply.Release();
        }
    }

    /// <summary>Gives up ownership and closes any open session.</summary>
    internal void Stop()
    {
        _apply.Wait();
        try
        {
            IDisposable? session;
            lock (_gate)
            {
                _owned = false;
                session = _session;
                _session = null;
            }

            session?.Dispose();
        }
        finally
        {
            _apply.Release();
        }
    }

    /// <summary>Records whether a consumer wants motion and opens or closes the session to match.</summary>
    /// <param name="wanted">Whether anything reads motion.</param>
    internal void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            _wanted = wanted;
        }

        _apply.Wait();
        try
        {
            bool reopen;
            IDisposable? close = null;
            lock (_gate)
            {
                var want = _owned && _wanted;
                reopen = want && _session is null;
                if (!want && _session is not null)
                {
                    close = _session;
                    _session = null;
                }
            }

            close?.Dispose();
            if (!reopen)
            {
                return;
            }

            var session = open();
            if (session is null)
            {
                return;
            }

            bool keep;
            lock (_gate)
            {
                keep = _owned && _wanted && _session is null;
                if (keep)
                {
                    _session = session;
                }
            }

            if (!keep)
            {
                session.Dispose();
            }
        }
        finally
        {
            _apply.Release();
        }
    }

    private IDisposable? Current()
    {
        lock (_gate)
        {
            return _session;
        }
    }
}
