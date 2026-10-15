using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

/// <summary>
/// GH-5016. RavenDb's part in an externally triggered durability pass under
/// <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
/// <remarks>
/// There is no RavenDb equivalent of <see cref="DurabilitySettings.EnableOutgoingTimestampIndex" />: RavenDb
/// builds and maintains an auto index for the stale owner-0 query on its own, so the flag changes nothing here
/// </remarks>
public partial class RavenDbMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource
{
    // How long a pass lock left behind by a crashed pass blocks the next one
    private static readonly TimeSpan DurabilityPassLockExpiration = TimeSpan.FromMinutes(5);

    internal string DurabilityPassLockId => "wolverine/durability-pass/" + _options.ServiceName.ToLowerInvariant();

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        var lockId = DurabilityPassLockId;
        var newLock = new DistributedLock
        {
            NodeId = _options.UniqueNodeId,
            ExpirationTime = DateTimeOffset.UtcNow.Add(DurabilityPassLockExpiration)
        };

        var result = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(lockId, newLock, 0), token: token);
        if (result.Successful)
        {
            return new DurabilityPassLock(_store, lockId, result.Index);
        }

        long index = 0;
        var taken = await tryTakeOverIfExpiredAsync(lockId, newLock, _ => { }, i => index = i, token);
        return taken ? new DurabilityPassLock(_store, lockId, index) : null;
    }

    public IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime)
    {
        return
        [
            new RecoverStaleOutgoingStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverStaleOutgoingStep>())
        ];
    }

    public async Task<IReadOnlyList<Envelope>> LoadStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        if (HasDisposed) return [];

        using var session = _store.OpenAsyncSession();

        // The excluded rows are the few that already failed in this pass, so they are filtered out here
        var outgoing = await staleOutgoing(session, staleBefore)
            .OrderBy(x => x.Timestamp)
            .Take(limit + excluded.Length)
            .ToListAsync(token);

        var skipped = excluded.Select(x => x.ToString()).ToHashSet();
        return outgoing.Where(x => !skipped.Contains(x.Id)).Take(limit).Select(x => x.Read()).ToList();
    }

    public async Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        if (HasDisposed) return 0;

        using var session = _store.OpenAsyncSession();
        var count = await staleOutgoing(session, staleBefore).CountAsync(token);

        // Every excluded row failed to send and is still stale and owned by any node
        return Math.Max(0, count - excluded.Length);
    }

    private static IQueryable<OutgoingMessage> staleOutgoing(Raven.Client.Documents.Session.IAsyncDocumentSession session,
        DateTimeOffset? staleBefore)
    {
        var query = session.Query<OutgoingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.OwnerId == TransportConstants.AnyNode);

        if (staleBefore.HasValue)
        {
            var stale = staleBefore.Value;
            query = query.Where(x => x.Timestamp <= stale);
        }

        return query;
    }

    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly IDocumentStore _store;
        private readonly string _lockId;
        private readonly long _index;

        public DurabilityPassLock(IDocumentStore store, string lockId, long index)
        {
            _store = store;
            _lockId = lockId;
            _index = index;
        }

        public async ValueTask DisposeAsync()
        {
            await _store.Operations.SendAsync(
                new DeleteCompareExchangeValueOperation<DistributedLock>(_lockId, _index));
        }
    }
}
