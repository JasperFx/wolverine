using System.Collections.Concurrent;
using Wolverine.Persistence;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// A trivial in-memory <see cref="IClaimCheckStore"/> that records how many times it was
/// exercised. Used to prove that a DI-registered store is actually used by the claim-check
/// pipeline (rather than the file-system fallback) — see GH-3564.
/// </summary>
public sealed class RecordingInMemoryClaimCheckStore : IClaimCheckStore
{
    private readonly ConcurrentDictionary<string, byte[]> _payloads = new();

    public int StoreCount;
    public int LoadCount;
    public int DeleteCount;

    public Task<ClaimCheckToken> StoreAsync(ReadOnlyMemory<byte> payload, string contentType,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StoreCount);
        var id = Guid.NewGuid().ToString("N");
        _payloads[id] = payload.ToArray();
        return Task.FromResult(new ClaimCheckToken(id, contentType, payload.Length));
    }

    public Task<ReadOnlyMemory<byte>> LoadAsync(ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref LoadCount);
        if (!_payloads.TryGetValue(token.Id, out var bytes))
        {
            throw new KeyNotFoundException($"No claim-check payload stored under '{token.Id}'.");
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
    }

    public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref DeleteCount);
        _payloads.TryRemove(token.Id, out _);
        return Task.CompletedTask;
    }
}
