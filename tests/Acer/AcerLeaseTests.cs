using LibHandheld.Families.Acer;

namespace LibHandheld.Tests.Acer;

public sealed class AcerLeaseTests
{
    [Fact]
    public async Task HoldersShareOneTransportUntilTheLastRelease()
    {
        var opened = 0;
        var lease = new AcerLease<Transport>(() =>
        {
            opened++;
            return new Transport();
        });

        var first = lease.Acquire();
        var second = lease.Acquire();
        Assert.Same(first, second);
        Assert.Equal(1, opened);

        await lease.ReleaseAsync();
        Assert.False(first!.Disposed);
        await lease.ReleaseAsync();
        Assert.True(first.Disposed);

        var reopened = lease.Acquire();
        Assert.NotSame(first, reopened);
        Assert.Equal(2, opened);
    }

    [Fact]
    public async Task AnAbsentEndpointHoldsNothing()
    {
        var lease = new AcerLease<Transport>(() => null);

        Assert.Null(lease.Acquire());
        await lease.ReleaseAsync();
        var present = new AcerLease<Transport>(() => new Transport());
        var transport = present.Acquire();
        await present.ReleaseAsync();
        await present.ReleaseAsync();
        Assert.True(transport!.Disposed);
    }

    private sealed class Transport : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
