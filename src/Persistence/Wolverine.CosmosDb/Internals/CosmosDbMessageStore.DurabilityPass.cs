using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals;

/// <summary>
/// GH-5016. Cosmos DB's part in an externally triggered durability pass under
/// <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
/// <remarks>
/// There is no Cosmos DB equivalent of <see cref="DurabilitySettings.EnableOutgoingTimestampIndex" />: a
/// Cosmos DB container indexes every property by default, so the flag changes nothing here
/// </remarks>
public partial class CosmosDbMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource, IExpiredDocumentSource
{
    private ILogger _durabilityPassLogger = NullLogger.Instance;

    /// <summary>
    /// How long a pass lock left behind by a crashed pass blocks the next one. The holder renews it every
    /// quarter of this while its pass runs, so a pass that outlives it keeps the lock. See GH-5036
    /// </summary>
    public TimeSpan DurabilityPassLockTtl { get; set; } = TimeSpan.FromMinutes(5);

    internal TimeSpan KeepAfterMessageHandling => _options.Durability.KeepAfterMessageHandling;

    private static string staleOutgoingFilter(DateTimeOffset? staleBefore)
    {
        var filter = "c.docType = @docType AND c.ownerId = @owner";
        return staleBefore.HasValue
            ? filter + " AND (NOT IS_DEFINED(c.timestamp) OR IS_NULL(c.timestamp) OR c.timestamp <= @stale)"
            : filter;
    }

    internal string DurabilityPassLockId => $"lock|durability-pass|{_options.ServiceName.ToLowerInvariant()}";

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        var lockId = DurabilityPassLockId;
        var partition = new PartitionKey(DocumentTypes.SystemPartition);
        var newLock = newDurabilityPassLock();

        try
        {
            var response = await _container.CreateItemAsync(newLock, partition, cancellationToken: token);
            return startRenewing(response.ETag);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            // Held by another pass, unless that pass crashed and left an expired lock behind
        }

        try
        {
            var existing = await _container.ReadItemAsync<CosmosDistributedLock>(lockId, partition,
                cancellationToken: token);
            if (existing.Resource.ExpirationTime >= DateTimeOffset.UtcNow) return null;

            var replaced = await _container.ReplaceItemAsync(newLock, lockId, partition,
                new ItemRequestOptions { IfMatchEtag = existing.ETag }, token);
            return startRenewing(replaced.ETag);
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            // Released or taken over by another pass in the meantime
            return null;
        }
    }

    private CosmosDistributedLock newDurabilityPassLock()
    {
        return new CosmosDistributedLock
        {
            Id = DurabilityPassLockId,
            NodeId = _options.UniqueNodeId.ToString(),
            ExpirationTime = DateTimeOffset.UtcNow.Add(DurabilityPassLockTtl)
        };
    }

    private DurabilityPassLock startRenewing(string etag)
    {
        var passLock = new DurabilityPassLock(this, DurabilityPassLockId, etag);
        passLock.StartRenewing();
        return passLock;
    }

    public IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime)
    {
        _durabilityPassLogger = runtime.LoggerFactory.CreateLogger<CosmosDbMessageStore>();

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

        // The excluded rows are the few that already failed in this pass, so they are filtered out here
        var query = staleOutgoingQuery($"SELECT TOP @limit * FROM c WHERE {staleOutgoingFilter(staleBefore)} ORDER BY c.timestamp",
                staleBefore)
            .WithParameter("@limit", limit + excluded.Length);

        var outgoing = new List<OutgoingMessage>();
        using var iterator = _container.GetItemQueryIterator<OutgoingMessage>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            outgoing.AddRange(response);
        }

        var skipped = excluded.ToHashSet();
        return outgoing.Where(x => !skipped.Contains(x.EnvelopeId)).Take(limit).Select(x => x.Read()).ToList();
    }

    public async Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        if (HasDisposed) return 0;

        var query = staleOutgoingQuery($"SELECT VALUE COUNT(1) FROM c WHERE {staleOutgoingFilter(staleBefore)}", staleBefore);

        var count = 0;
        using var iterator = _container.GetItemQueryIterator<int>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            count += response.Sum();
        }

        // Every excluded row failed to send and is still stale and owned by any node
        return Math.Max(0, count - excluded.Length);
    }

    public async Task RestartStaleOutgoingAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token)
    {
        if (HasDisposed || envelopes.Count == 0) return;

        // The application's clock, the same one OutgoingMessage stamps a new document with
        var now = DateTimeOffset.UtcNow;
        var options = new PatchItemRequestOptions { EnableContentResponseOnWrite = false };

        foreach (var envelope in envelopes)
        {
            try
            {
                await _container.PatchItemAsync<OutgoingMessage>($"outgoing|{envelope.Id}",
                    new PartitionKey(envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition),
                    [PatchOperation.Set("/timestamp", now)], options, token);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }
    }

    private static QueryDefinition staleOutgoingQuery(string queryText, DateTimeOffset? staleBefore)
    {
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Outgoing)
            .WithParameter("@owner", TransportConstants.AnyNode);

        return staleBefore.HasValue ? query.WithParameter("@stale", staleBefore.Value.ToUniversalTime()) : query;
    }

    public Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        var query = new QueryDefinition(
                $"SELECT TOP @limit * FROM c WHERE {DueScheduledFilter} ORDER BY c.executionTime")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Scheduled)
            .WithParameter("@now", now.ToUniversalTime())
            .WithParameter("@limit", limit + excluded.Length);

        return loadIncomingAsync(query, excluded, limit, token);
    }

    public Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {DueScheduledFilter}")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Scheduled)
            .WithParameter("@now", now.ToUniversalTime());

        // Every excluded document failed in this pass and is still scheduled and due
        return countAsync(query, excluded, token);
    }

    // Cosmos DB keeps no inbox stale clock, so staleBefore makes no difference: only documents owned by any
    // node are recovered, which never races a live request
    public Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore,
        Guid[] excluded, int limit, CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT TOP @limit * FROM c WHERE {RecoverableIncomingFilter}")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Incoming)
            .WithParameter("@owner", TransportConstants.AnyNode)
            .WithParameter("@limit", limit + excluded.Length);

        return loadIncomingAsync(query, excluded, limit, token);
    }

    public Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {RecoverableIncomingFilter}")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Incoming)
            .WithParameter("@owner", TransportConstants.AnyNode);

        // Every excluded document failed in this pass and is still incoming and owned by any node
        return countAsync(query, excluded, token);
    }

    private const string DueScheduledFilter =
        "c.docType = @docType AND c.status = @status AND c.executionTime <= @now";

    private const string RecoverableIncomingFilter =
        "c.docType = @docType AND c.status = @status AND c.ownerId = @owner";

    private async Task<IReadOnlyList<Envelope>> loadIncomingAsync(QueryDefinition query, Guid[] excluded, int limit,
        CancellationToken token)
    {
        if (HasDisposed) return [];

        var incoming = new List<IncomingMessage>();
        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            incoming.AddRange(response);
        }

        var skipped = excluded.ToHashSet();
        var envelopes = incoming.Where(x => !skipped.Contains(x.EnvelopeId)).Take(limit).Select(x => x.Read())
            .ToList();

        // Marked as handled against this store, which matters for an ancillary store. See GH-4216
        foreach (var envelope in envelopes) envelope.Store = this;

        return envelopes;
    }

    private async Task<int> countAsync(QueryDefinition query, Guid[] excluded, CancellationToken token)
    {
        if (HasDisposed) return 0;

        var count = 0;
        using var iterator = _container.GetItemQueryIterator<int>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            count += response.Sum();
        }

        return Math.Max(0, count - excluded.Length);
    }

    // keepUntil is compared as a timestamp, not as a string, for the reason given on
    // CosmosDbDurabilityAgent.tryDeleteExpiredHandledEnvelopes
    private const string ExpiredHandledFilter =
        "c.docType = @docType AND c.status = @status AND DateTimeToTimestamp(c.keepUntil) <= @now";

    public async Task<int> DeleteExpiredHandledAsync(DateTimeOffset now, int limit, CancellationToken token)
    {
        if (HasDisposed) return 0;

        // Necessarily cross-partition, so each delete carries the document's own partition key
        var query = new QueryDefinition($"SELECT TOP @limit c.id, c.partitionKey FROM c WHERE {ExpiredHandledFilter}")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Handled)
            .WithParameter("@now", now.ToUnixTimeMilliseconds())
            .WithParameter("@limit", limit);

        var expired = new List<(string Id, string PartitionKey)>();
        using (var iterator = _container.GetItemQueryIterator<dynamic>(query))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(token);
                foreach (var item in response)
                {
                    expired.Add(((string)item.id, (string)item.partitionKey));
                }
            }
        }

        var deleted = 0;
        foreach (var (id, partitionKey) in expired)
        {
            try
            {
                await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey),
                    cancellationToken: token);
                deleted++;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }

        return deleted;
    }

    public Task<int> CountExpiredHandledAsync(DateTimeOffset now, CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {ExpiredHandledFilter}")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Handled)
            .WithParameter("@now", now.ToUnixTimeMilliseconds());

        return countAsync(query, [], token);
    }

    public async Task<int> DeleteExpiredDeadLettersAsync(DateTimeOffset now, CancellationToken token)
    {
        if (HasDisposed) return 0;

        var query = new QueryDefinition(
                "SELECT c.id FROM c WHERE c.docType = @docType AND c.expirationTime < @now")
            .WithParameter("@docType", DocumentTypes.DeadLetter)
            .WithParameter("@now", now.ToUniversalTime());

        var partition = new PartitionKey(DocumentTypes.DeadLetterPartition);
        var expired = new List<string>();
        using (var iterator = _container.GetItemQueryIterator<dynamic>(query,
                   requestOptions: new QueryRequestOptions { PartitionKey = partition }))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(token);
                foreach (var item in response) expired.Add((string)item.id);
            }
        }

        var deleted = 0;
        foreach (var id in expired)
        {
            try
            {
                await _container.DeleteItemAsync<dynamic>(id, partition, cancellationToken: token);
                deleted++;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }

        return deleted;
    }

    /// <summary>
    /// The lock document is renewed every quarter of its TTL while the pass runs, each time against the ETag
    /// this holder last wrote, so a holder that lost it to a takeover can neither revive it nor delete the
    /// document of the pass that took it over. See GH-5036
    /// </summary>
    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly CosmosDbMessageStore _parent;
        private readonly string _lockId;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cancellation = new();
        private string _etag;
        private Task? _renewing;

        public DurabilityPassLock(CosmosDbMessageStore parent, string lockId, string etag)
        {
            _parent = parent;
            _lockId = lockId;
            _etag = etag;
        }

        private static PartitionKey Partition => new(DocumentTypes.SystemPartition);

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

                    var renewed = await _parent._container.ReplaceItemAsync(_parent.newDurabilityPassLock(), _lockId,
                        Partition, new ItemRequestOptions { IfMatchEtag = _etag }, _cancellation.Token);
                    _etag = renewed.ETag;
                }
                catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound
                                                    or HttpStatusCode.PreconditionFailed)
                {
                    // Taken over, so another pass may hold the lock now. Nothing more to renew
                    _parent._durabilityPassLogger.LogWarning(
                        "Lost the durability pass lock for message store {Store} while the pass was still running",
                        _parent.Uri);
                    return;
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

            // The renewal loop catches everything, and it has to be done before the document goes
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            if (_renewing != null) await _renewing;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

            await _gate.WaitAsync();
            try
            {
                await _parent._container.DeleteItemAsync<CosmosDistributedLock>(_lockId, Partition,
                    new ItemRequestOptions { IfMatchEtag = _etag });
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
                // Expired and taken over by another pass, which now owns it
            }
            finally
            {
                _gate.Release();
                _cancellation.Dispose();
            }
        }
    }
}
