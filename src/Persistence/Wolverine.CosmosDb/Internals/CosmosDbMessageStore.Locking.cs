using System.Net;
using Newtonsoft.Json;
using Microsoft.Azure.Cosmos;
using Wolverine.Runtime;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore
{
    private string _leaderLockId;
    private string _scheduledLockId;
    private CosmosDistributedLock? _scheduledLock;
    private string? _scheduledLockETag;
    private IWolverineRuntime? _runtime;

    private CosmosDistributedLock? _leaderLock;
    private string? _leaderLockETag;

    public bool HasLeadershipLock()
    {
        if (_leaderLock == null) return false;
        if (_leaderLock.ExpirationTime < DateTimeOffset.UtcNow) return false;
        return true;
    }

    public async Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        var newLock = new CosmosDistributedLock
        {
            Id = _leaderLockId,
            NodeId = _options.UniqueNodeId.ToString(),
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        if (_leaderLock == null)
        {
            try
            {
                var response = await _container.CreateItemAsync(newLock,
                    new PartitionKey(DocumentTypes.SystemPartition), cancellationToken: token);
                _leaderLock = newLock;
                _leaderLockETag = response.ETag;
                return true;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
            {
                // Lock already exists, try to take it if expired
                try
                {
                    var existing = await _container.ReadItemAsync<CosmosDistributedLock>(
                        _leaderLockId, new PartitionKey(DocumentTypes.SystemPartition),
                        cancellationToken: token);

                    if (existing.Resource.ExpirationTime < DateTimeOffset.UtcNow)
                    {
                        var replaceResponse = await _container.ReplaceItemAsync(newLock,
                            _leaderLockId, new PartitionKey(DocumentTypes.SystemPartition),
                            new ItemRequestOptions { IfMatchEtag = existing.ETag },
                            cancellationToken: token);
                        _leaderLock = newLock;
                        _leaderLockETag = replaceResponse.ETag;
                        return true;
                    }
                }
                catch (CosmosException)
                {
                    // Another node took it
                }

                return false;
            }
        }

        try
        {
            var response = await _container.ReplaceItemAsync(newLock, _leaderLockId,
                new PartitionKey(DocumentTypes.SystemPartition),
                new ItemRequestOptions { IfMatchEtag = _leaderLockETag },
                cancellationToken: token);
            _leaderLock = newLock;
            _leaderLockETag = response.ETag;
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed ||
                                        e.StatusCode == HttpStatusCode.NotFound)
        {
            _leaderLock = null;
            _leaderLockETag = null;
            return false;
        }
    }

    public async Task ReleaseLeadershipLockAsync()
    {
        if (_leaderLock == null) return;
        try
        {
            await _container.DeleteItemAsync<CosmosDistributedLock>(
                _leaderLockId, new PartitionKey(DocumentTypes.SystemPartition),
                new ItemRequestOptions { IfMatchEtag = _leaderLockETag });
        }
        catch (CosmosException)
        {
            // Best effort
        }

        _leaderLock = null;
        _leaderLockETag = null;
    }

    public async Task<bool> TryAttainScheduledJobLockAsync(CancellationToken token)
    {
        var newLock = new CosmosDistributedLock
        {
            Id = _scheduledLockId,
            NodeId = _options.UniqueNodeId.ToString(),
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        if (_scheduledLock == null)
        {
            try
            {
                var response = await _container.CreateItemAsync(newLock,
                    new PartitionKey(DocumentTypes.SystemPartition), cancellationToken: token);
                _scheduledLock = newLock;
                _scheduledLockETag = response.ETag;
                return true;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
            {
                try
                {
                    var existing = await _container.ReadItemAsync<CosmosDistributedLock>(
                        _scheduledLockId, new PartitionKey(DocumentTypes.SystemPartition),
                        cancellationToken: token);

                    if (existing.Resource.ExpirationTime < DateTimeOffset.UtcNow)
                    {
                        var replaceResponse = await _container.ReplaceItemAsync(newLock,
                            _scheduledLockId, new PartitionKey(DocumentTypes.SystemPartition),
                            new ItemRequestOptions { IfMatchEtag = existing.ETag },
                            cancellationToken: token);
                        _scheduledLock = newLock;
                        _scheduledLockETag = replaceResponse.ETag;
                        return true;
                    }
                }
                catch (CosmosException)
                {
                    // Another node took it
                }

                return false;
            }
        }

        try
        {
            var response = await _container.ReplaceItemAsync(newLock, _scheduledLockId,
                new PartitionKey(DocumentTypes.SystemPartition),
                new ItemRequestOptions { IfMatchEtag = _scheduledLockETag },
                cancellationToken: token);
            _scheduledLock = newLock;
            _scheduledLockETag = response.ETag;
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed ||
                                        e.StatusCode == HttpStatusCode.NotFound)
        {
            _scheduledLock = null;
            _scheduledLockETag = null;
            return false;
        }
    }

    public async Task ReleaseScheduledJobLockAsync()
    {
        if (_scheduledLock == null) return;
        try
        {
            await _container.DeleteItemAsync<CosmosDistributedLock>(
                _scheduledLockId, new PartitionKey(DocumentTypes.SystemPartition),
                new ItemRequestOptions { IfMatchEtag = _scheduledLockETag });
        }
        catch (CosmosException)
        {
            // Best effort
        }

        _scheduledLock = null;
        _scheduledLockETag = null;
    }
}

public class CosmosDistributedLock
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.Lock;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = DocumentTypes.SystemPartition;

    [JsonProperty("nodeId")] public string NodeId { get; set; } = string.Empty;

    [JsonProperty("expirationTime")] public DateTimeOffset ExpirationTime { get; set; }
}
