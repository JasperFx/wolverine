using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
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
public partial class CosmosDbMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource
{
    // How long a pass lock left behind by a crashed pass blocks the next one
    private static readonly TimeSpan DurabilityPassLockExpiration = TimeSpan.FromMinutes(5);

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
        var newLock = new CosmosDistributedLock
        {
            Id = lockId,
            NodeId = _options.UniqueNodeId.ToString(),
            ExpirationTime = DateTimeOffset.UtcNow.Add(DurabilityPassLockExpiration)
        };

        try
        {
            var response = await _container.CreateItemAsync(newLock, partition, cancellationToken: token);
            return new DurabilityPassLock(_container, lockId, response.ETag);
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
            return new DurabilityPassLock(_container, lockId, replaced.ETag);
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            // Released or taken over by another pass in the meantime
            return null;
        }
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

    private static QueryDefinition staleOutgoingQuery(string queryText, DateTimeOffset? staleBefore)
    {
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Outgoing)
            .WithParameter("@owner", TransportConstants.AnyNode);

        return staleBefore.HasValue ? query.WithParameter("@stale", staleBefore.Value.ToUniversalTime()) : query;
    }

    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly Container _container;
        private readonly string _lockId;
        private readonly string _etag;

        public DurabilityPassLock(Container container, string lockId, string etag)
        {
            _container = container;
            _lockId = lockId;
            _etag = etag;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _container.DeleteItemAsync<CosmosDistributedLock>(_lockId,
                    new PartitionKey(DocumentTypes.SystemPartition),
                    new ItemRequestOptions { IfMatchEtag = _etag });
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
            {
                // Expired and taken over by another pass, which now owns it
            }
        }
    }
}
