using System.Collections.Concurrent;
using Wolverine.Persistence;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// An in-memory <see cref="IClaimCheckStoreWithExpiration"/> whose payload timestamps can be back-dated,
/// so a test can prove the GH-3509 sweeper deletes aged payloads without waiting out a real TTL.
/// </summary>
public sealed class ExpiringInMemoryClaimCheckStore : IClaimCheckStoreWithExpiration
{
    private readonly ConcurrentDictionary<string, Entry> _payloads = new();

    public int SweepCount;

    private sealed record Entry(byte[] Payload, DateTimeOffset Created);

    public IReadOnlyCollection<string> Ids => _payloads.Keys.ToList();

    public int Count => _payloads.Count;

    /// <summary>Back-date a stored payload so it looks older than the sweeper's cutoff.</summary>
    public void Age(string id, TimeSpan by)
    {
        if (_payloads.TryGetValue(id, out var entry))
        {
            _payloads[id] = entry with { Created = entry.Created - by };
        }
    }

    public Task<ClaimCheckToken> StoreAsync(ReadOnlyMemory<byte> payload, string contentType,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        _payloads[id] = new Entry(payload.ToArray(), DateTimeOffset.UtcNow);
        return Task.FromResult(new ClaimCheckToken(id, contentType, payload.Length));
    }

    public Task<ReadOnlyMemory<byte>> LoadAsync(ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        if (!_payloads.TryGetValue(token.Id, out var entry))
        {
            throw new KeyNotFoundException($"No claim-check payload stored under '{token.Id}'.");
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(entry.Payload);
    }

    public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        _payloads.TryRemove(token.Id, out _);
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredPayloadsAsync(DateTimeOffset cutoff, int maxCount,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref SweepCount);

        var deleted = 0;
        foreach (var pair in _payloads)
        {
            if (deleted >= maxCount)
            {
                break;
            }

            if (pair.Value.Created < cutoff && _payloads.TryRemove(pair.Key, out _))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }
}

/// <summary>
/// A store that succeeds for the first <see cref="FailAfter"/> uploads and then throws, so a test can
/// assert that a partially-completed off-load cleans up the payloads it already wrote (GH-3509).
/// </summary>
public sealed class FailAfterNClaimCheckStore : IClaimCheckStore
{
    private readonly ConcurrentDictionary<string, byte[]> _payloads = new();
    private int _stored;

    public int FailAfter { get; init; } = 1;

    public int Count => _payloads.Count;
    public int DeleteCount;

    public Task<ClaimCheckToken> StoreAsync(ReadOnlyMemory<byte> payload, string contentType,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _stored) > FailAfter)
        {
            throw new InvalidOperationException("Simulated claim-check storage failure.");
        }

        var id = Guid.NewGuid().ToString("N");
        _payloads[id] = payload.ToArray();
        return Task.FromResult(new ClaimCheckToken(id, contentType, payload.Length));
    }

    public Task<ReadOnlyMemory<byte>> LoadAsync(ClaimCheckToken token,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ReadOnlyMemory<byte>>(_payloads[token.Id]);

    public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref DeleteCount);
        _payloads.TryRemove(token.Id, out _);
        return Task.CompletedTask;
    }
}
