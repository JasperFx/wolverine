using Raven.Client.Documents.Operations.CompareExchange;
using Wolverine.Runtime;

namespace Wolverine.RavenDb.Internals;

// TODO -- harden all locking methods
public partial class RavenDbMessageStore
{
    private string _leaderLockId = null!;
    private string _scheduledLockId = null!;
    private long _lastScheduledLockIndex = 0;
    private DistributedLock? _scheduledLock;
    private IWolverineRuntime _runtime = null!;

    private DistributedLock? _leaderLock;
    private long _lastLockIndex = 0;
    

    public bool HasLeadershipLock()
    {
        if (_leaderLock == null) return false;
        if (_leaderLock.ExpirationTime < DateTimeOffset.UtcNow) return false;
        return true;
    }

    // Error handling is outside of this
    public async Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        var newLock = new DistributedLock
        {
            NodeId = _options.UniqueNodeId,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        if (_leaderLock == null)
        {
            var result = await _store.Operations.SendAsync(new PutCompareExchangeValueOperation<DistributedLock>(_leaderLockId, newLock, 0), token: token);
            if (result.Successful)
            {
                _leaderLock = newLock;
                _lastLockIndex = result.Index;
                return true;
            }

            return await tryTakeOverIfExpiredAsync(_leaderLockId, newLock, lockSet: l => _leaderLock = l, indexSet: i => _lastLockIndex = i, token);
        }

        var result2 = await _store.Operations.SendAsync(new PutCompareExchangeValueOperation<DistributedLock>(_leaderLockId, newLock, _lastLockIndex), token: token);
        if (result2.Successful)
        {
            _leaderLock = newLock;
            _lastLockIndex = result2.Index;
            return true;
        }

        return false;
    }

    // Error handling is outside of this
    public async Task ReleaseLeadershipLockAsync()
    {
        if (_leaderLock == null) return;
        await _store.Operations.SendAsync(new DeleteCompareExchangeValueOperation<DistributedLock>(_leaderLockId, _lastLockIndex));
        _leaderLock = null;
    }

    // Error handling is outside of this
    public async Task<bool> TryAttainScheduledJobLockAsync(CancellationToken token)
    {
        var newLock = new DistributedLock
        {
            NodeId = _options.UniqueNodeId,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        
        if (_scheduledLock == null)
        {
            var result = await _store.Operations.SendAsync(new PutCompareExchangeValueOperation<DistributedLock>(_scheduledLockId, newLock, 0), token: token);
            if (result.Successful)
            {
                _scheduledLock = newLock;
                _lastScheduledLockIndex = result.Index;
                return true;
            }

            return await tryTakeOverIfExpiredAsync(_scheduledLockId, newLock, lockSet: l => _scheduledLock = l, indexSet: i => _lastScheduledLockIndex = i, token);
        }
        
        var result2 = await _store.Operations.SendAsync(new PutCompareExchangeValueOperation<DistributedLock>(_scheduledLockId, newLock, _lastScheduledLockIndex), token: token);
        if (result2.Successful)
        {
            _scheduledLock = newLock;
            _lastScheduledLockIndex = result2.Index;
            return true;
        }

        return false;
    }

    public async Task ReleaseScheduledJobLockAsync()
    {
        if (_scheduledLock == null) return;
        await _store.Operations.SendAsync(new DeleteCompareExchangeValueOperation<DistributedLock>(_scheduledLockId, _lastScheduledLockIndex));
        _scheduledLock = null;
    }

    // A predecessor process can crash without releasing its lock, leaving the CE value
    // behind indefinitely. The DistributedLock.ExpirationTime field is the recovery
    // hook: if the existing lock is past its expiration, CAS-replace it using the
    // current CE index. Mirrors the equivalent path in CosmosDbMessageStore.Locking.
    private async Task<bool> tryTakeOverIfExpiredAsync(string lockId, DistributedLock newLock, Action<DistributedLock> lockSet, Action<long> indexSet, CancellationToken token)
    {
        var existing = await _store.Operations.SendAsync(new GetCompareExchangeValueOperation<DistributedLock>(lockId), token: token);
        if (existing?.Value == null) return false;
        if (existing.Value.ExpirationTime > DateTimeOffset.UtcNow) return false;

        var takeover = await _store.Operations.SendAsync(new PutCompareExchangeValueOperation<DistributedLock>(lockId, newLock, existing.Index), token: token);
        if (!takeover.Successful) return false;

        lockSet(newLock);
        indexSet(takeover.Index);
        return true;
    }
}

public class DistributedLock
{
    public Guid NodeId { get; set; }
    public DateTimeOffset ExpirationTime { get; set; } 
}