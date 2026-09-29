namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-4659. A per-store cool-down in front of a tenant database that has just refused a write.
/// </summary>
/// <remarks>
/// <para>
/// When a tenant's database is down, <see cref="MultiTenantedMessageStore" /> reports the failure as
/// tenant-scoped, the receiver defers that envelope back to the broker, and the broker hands it straight
/// back — on every transport, with nothing in between. The envelope therefore goes round as fast as the
/// database can refuse a connection: the reporter measured ~300 turns a second from a *single* stranded
/// message on PostgreSQL, 620/s on SQL Server, and a hundred stranded messages taking a Windows host to
/// ~7,000 of its ~15,700 ephemeral ports in TIME_WAIT — ports shared with the broker, the telemetry, and
/// every healthy tenant's connections.
/// </para>
///
/// <para>
/// This brake makes the refusal free. Once a store has failed, its writes are refused for a cycle
/// <b>without opening a connection</b>, and exactly one probe per cycle is let through to find out whether
/// the database is back. A success — or a duplicate, which also proves the database answered — releases it.
/// </para>
///
/// <para>
/// It deliberately does <b>not</b> slow the redelivery down. The tenant-scoped defer runs inline on the
/// listener's own single-worker dispatch thread (<c>RetryBlock.PostAsync</c> executes its handler on the
/// calling thread), so anything that waits there stalls every other tenant on that endpoint — and on
/// RabbitMQ the defer callback is shared across every listener on the connection. Refusing immediately is
/// what keeps this safe: the message still goes round, it just stops costing a connection each time.
/// </para>
/// </remarks>
internal sealed class TenantWriteBrake
{
    private readonly TimeSpan _cycle;

    private long _nextProbeTicks;
    private volatile bool _tripped;

    public TenantWriteBrake(TimeSpan cycle)
    {
        _cycle = cycle;
    }

    /// <summary>
    /// True when this write may attempt the database. False means the store is in its cool-down and the
    /// caller must fail fast without connecting.
    /// </summary>
    public bool TryEnter()
    {
        if (!_tripped)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow.Ticks;
        var next = Interlocked.Read(ref _nextProbeTicks);

        if (now < next)
        {
            return false;
        }

        // Exactly one caller per cycle wins the probe; the losers of the race are refused for free, which
        // is the whole point when a hundred stranded messages are all spinning at once.
        return Interlocked.CompareExchange(ref _nextProbeTicks, now + _cycle.Ticks, next) == next;
    }

    /// <summary>The store refused a write. Hold its writes for a cycle.</summary>
    public void Trip()
    {
        Interlocked.Exchange(ref _nextProbeTicks, DateTimeOffset.UtcNow.Ticks + _cycle.Ticks);
        _tripped = true;
    }

    /// <summary>The store answered. Let everything through again.</summary>
    public void Release()
    {
        if (_tripped)
        {
            _tripped = false;
        }
    }
}
