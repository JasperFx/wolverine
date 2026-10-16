using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Session;
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
public partial class RavenDbMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource, IExpiredDocumentSource
{
    private ILogger _durabilityPassLogger = NullLogger.Instance;

    /// <summary>
    /// How long a pass lock left behind by a crashed pass blocks the next one. The holder renews it every
    /// quarter of this while its pass runs, so a pass that outlives it keeps the lock. See GH-5036
    /// </summary>
    public TimeSpan DurabilityPassLockTtl { get; set; } = TimeSpan.FromMinutes(5);

    internal TimeSpan KeepAfterMessageHandling => _options.Durability.KeepAfterMessageHandling;

    // GH-5038: one lock per database, not per service. The stale outbox and inbox queries are not scoped by
    // service, so two services sharing a database ran passes over the same owner 0 documents at once. The lock
    // document lives in this database, so a fixed id is per database, as the SQL stores key on their schema
    internal string DurabilityPassLockId => "wolverine/durability-pass";

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        var lockId = DurabilityPassLockId;
        var newLock = newDurabilityPassLock();

        var result = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(lockId, newLock, 0), token: token);

        long index = 0;
        if (result.Successful)
        {
            index = result.Index;
        }
        else if (!await tryTakeOverIfExpiredAsync(lockId, newLock, _ => { }, i => index = i, token))
        {
            return null;
        }

        var passLock = new DurabilityPassLock(this, lockId, index);
        passLock.StartRenewing();
        return passLock;
    }

    private DistributedLock newDurabilityPassLock()
    {
        return new DistributedLock
        {
            NodeId = _options.UniqueNodeId,
            ExpirationTime = DateTimeOffset.UtcNow.Add(DurabilityPassLockTtl)
        };
    }

    public IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime)
    {
        _durabilityPassLogger = runtime.LoggerFactory.CreateLogger<RavenDbMessageStore>();

        return
        [
            new RecoverStaleOutgoingStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverStaleOutgoingStep>()),
            new ReleaseScheduledMessagesStep(this, this, runtime.LoggerFactory.CreateLogger<ReleaseScheduledMessagesStep>()),
            new RecoverIncomingMessagesStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverIncomingMessagesStep>()),
            new DeleteExpiredDocumentsStep(this, this, runtime.LoggerFactory.CreateLogger<DeleteExpiredDocumentsStep>())
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

    public async Task RestartStaleOutgoingAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token)
    {
        if (HasDisposed || envelopes.Count == 0) return;

        // The application's clock, the same one OutgoingMessage stamps a new document with
        var now = DateTimeOffset.UtcNow;

        using var session = _store.OpenAsyncSession();
        foreach (var envelope in envelopes)
        {
            session.Advanced.Patch<OutgoingMessage, DateTimeOffset?>(envelope.Id.ToString(), x => x.Timestamp, now);
        }

        await session.SaveChangesAsync(token);
    }

    private static IAsyncDocumentQuery<OutgoingMessage> staleOutgoing(IAsyncDocumentSession session,
        DateTimeOffset? staleBefore)
    {
        var query = session.Advanced.AsyncDocumentQuery<OutgoingMessage>()
            .WaitForNonStaleResults()
            .WhereEquals(x => x.OwnerId, TransportConstants.AnyNode);

        if (staleBefore.HasValue)
        {
            // GH-5036. A document written before Timestamp existed does not have the field at all, and RavenDb
            // matches neither a range nor "= null" against a missing field, only "not exists". Those always
            // count as stale
            query = query.AndAlso().OpenSubclause()
                .WhereLessThanOrEqual(x => x.Timestamp, staleBefore.Value)
                .OrElse().WhereEquals(x => x.Timestamp, (DateTimeOffset?)null)
                .OrElse().Not.WhereExists(x => x.Timestamp)
                .CloseSubclause();
        }

        return query;
    }

    public async Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        if (HasDisposed) return [];

        using var session = _store.OpenAsyncSession();
        var scheduled = await dueScheduled(session, now)
            .OrderBy(x => x.ExecutionTime)
            .Take(limit + excluded.Length)
            .ToListAsync(token);

        return readIncoming(scheduled, excluded, limit);
    }

    public async Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        if (HasDisposed) return 0;

        using var session = _store.OpenAsyncSession();
        var count = await dueScheduled(session, now).CountAsync(token);

        // Every excluded document failed in this pass and is still scheduled and due
        return Math.Max(0, count - excluded.Length);
    }

    // RavenDb keeps no inbox stale clock, so staleBefore makes no difference: only documents owned by any node
    // are recovered, which never races a live request
    public async Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore,
        Guid[] excluded, int limit, CancellationToken token)
    {
        if (HasDisposed) return [];

        using var session = _store.OpenAsyncSession();
        var incoming = await recoverableIncoming(session)
            .OrderBy(x => x.EnvelopeId)
            .Take(limit + excluded.Length)
            .ToListAsync(token);

        return readIncoming(incoming, excluded, limit);
    }

    public async Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        if (HasDisposed) return 0;

        using var session = _store.OpenAsyncSession();
        var count = await recoverableIncoming(session).CountAsync(token);

        // Every excluded document failed in this pass and is still incoming and owned by any node
        return Math.Max(0, count - excluded.Length);
    }

    private IReadOnlyList<Envelope> readIncoming(List<IncomingMessage> documents, Guid[] excluded, int limit)
    {
        var skipped = excluded.ToHashSet();
        var envelopes = documents.Where(x => !skipped.Contains(x.EnvelopeId)).Take(limit).Select(x => x.Read())
            .ToList();

        // Marked as handled against this store, which matters for an ancillary store. See GH-4216
        foreach (var envelope in envelopes) envelope.Store = this;

        return envelopes;
    }

    private static IQueryable<IncomingMessage> dueScheduled(IAsyncDocumentSession session, DateTimeOffset now)
    {
        return session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Scheduled && x.ExecutionTime <= now);
    }

    private static IQueryable<IncomingMessage> recoverableIncoming(IAsyncDocumentSession session)
    {
        return session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Incoming && x.OwnerId == TransportConstants.AnyNode);
    }

    // Handled documents written before GH-5036 have no KeepUntil, only the @expires that RavenDb's own
    // expiration feature reads, so they are left to that
    public async Task<int> DeleteExpiredHandledAsync(DateTimeOffset now, int limit, CancellationToken token)
    {
        if (HasDisposed) return 0;

        using var session = _store.OpenAsyncSession();
        var expired = await expiredHandled(session, now).Take(limit).ToListAsync(token);
        if (expired.Count == 0) return 0;

        foreach (var document in expired) session.Delete(document);
        await session.SaveChangesAsync(token);

        return expired.Count;
    }

    public async Task<int> CountExpiredHandledAsync(DateTimeOffset now, CancellationToken token)
    {
        if (HasDisposed) return 0;

        using var session = _store.OpenAsyncSession();
        return await expiredHandled(session, now).CountAsync(token);
    }

    private static IQueryable<IncomingMessage> expiredHandled(IAsyncDocumentSession session, DateTimeOffset now)
    {
        return session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.Status == EnvelopeStatus.Handled && x.KeepUntil <= now);
    }

    public async Task<int> DeleteExpiredDeadLettersAsync(DateTimeOffset now, CancellationToken token)
    {
        if (HasDisposed) return 0;

        var deleted = 0;
        while (!token.IsCancellationRequested)
        {
            using var session = _store.OpenAsyncSession();
            var expired = await session.Query<DeadLetterMessage>()
                .Customize(x => x.WaitForNonStaleResults())
                .Where(x => x.ExpirationTime < now)
                .Take(_options.Durability.RecoveryBatchSize)
                .ToListAsync(token);

            if (expired.Count == 0) break;

            foreach (var message in expired) session.Delete(message);
            await session.SaveChangesAsync(token);
            deleted += expired.Count;

            if (expired.Count < _options.Durability.RecoveryBatchSize) break;
        }

        return deleted;
    }

    /// <summary>
    /// The compare exchange value is renewed every quarter of its TTL while the pass runs, each time against
    /// the index this holder last wrote, so a holder that lost it to a takeover can neither revive it nor delete
    /// the value of the pass that took it over. See GH-5036
    /// </summary>
    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly RavenDbMessageStore _parent;
        private readonly string _lockId;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cancellation = new();
        private long _index;
        private Task? _renewing;

        public DurabilityPassLock(RavenDbMessageStore parent, string lockId, long index)
        {
            _parent = parent;
            _lockId = lockId;
            _index = index;
        }

        public void StartRenewing()
        {
            _renewing = Task.Run(renewAsync);
        }

        private async Task renewAsync()
        {
            var interval = _parent.DurabilityPassLockTtl / 4;
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await _gate.WaitAsync();
                try
                {
                    if (_cancellation.IsCancellationRequested) return;

                    var result = await _parent._store.Operations.SendAsync(
                        new PutCompareExchangeValueOperation<DistributedLock>(_lockId,
                            _parent.newDurabilityPassLock(), _index), token: _cancellation.Token);

                    if (!result.Successful)
                    {
                        // Taken over, so another pass may hold the lock now. Nothing more to renew
                        _parent._durabilityPassLogger.LogWarning(
                            "Lost the durability pass lock for message store {Store} while the pass was still running",
                            _parent.Uri);
                        return;
                    }

                    _index = result.Index;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Try again on the next tick, well inside the TTL
                    _parent._durabilityPassLogger.LogError(e,
                        "Unable to renew the durability pass lock for message store {Store}", _parent.Uri);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            // The renewal loop catches everything, and it has to be done before the value goes
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            if (_renewing != null) await _renewing;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

            await _gate.WaitAsync();
            try
            {
                await _parent._store.Operations.SendAsync(
                    new DeleteCompareExchangeValueOperation<DistributedLock>(_lockId, _index));
            }
            finally
            {
                _gate.Release();
                _cancellation.Dispose();
            }
        }
    }
}
