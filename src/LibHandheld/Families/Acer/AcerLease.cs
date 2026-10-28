namespace LibHandheld.Families.Acer;

/// <summary>One per-engine Atlas lane shared by the services that use it.</summary>
/// <typeparam name="T">The transport the lane opens.</typeparam>
/// <remarks>
///     The first holder opens the lane and the last release closes it, so services that share an EC or
///     controller endpoint enumerate it once and keep one handle. The engine serializes lifecycle calls.
/// </remarks>
internal interface IAcerLease<out T> where T : class
{
    /// <summary>Opens the lane on first use and takes one hold on it.</summary>
    /// <returns>The shared transport, or null without a hold when the endpoint is absent.</returns>
    T? Acquire();

    /// <summary>Drops one hold; the last one disposes the transport.</summary>
    /// <returns>Completion after any disposal.</returns>
    ValueTask ReleaseAsync();
}

/// <inheritdoc />
internal sealed class AcerLease<T>(Func<T?> open) : IAcerLease<T> where T : class
{
    private T? _value;
    private int _holders;

    /// <inheritdoc />
    public T? Acquire()
    {
        _value ??= open();
        if (_value is not null)
        {
            _holders++;
        }

        return _value;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync()
    {
        if (_holders == 0 || --_holders > 0)
        {
            return;
        }

        var value = _value;
        _value = null;
        if (value is IAsyncDisposable asyncValue)
        {
            await asyncValue.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            (value as IDisposable)?.Dispose();
        }
    }
}
