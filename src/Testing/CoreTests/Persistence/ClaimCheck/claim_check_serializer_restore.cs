using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// The claim-check serializer-decorator must never leave the live in-memory message mutated,
/// even when off-loading fails partway through. <see cref="local_queue_round_trip"/> covers the
/// happy path (restore after a successful serialize); this covers the failure path: if the store
/// rejects one blob after an earlier blob on the same message was already cleared, the earlier
/// property must be restored before the exception propagates, otherwise a cleared property leaks
/// into subsequent in-process handling of the same Envelope.
/// </summary>
public class claim_check_serializer_restore
{
    private static ClaimCheckMessageSerializer NewSut(IClaimCheckStore store)
    {
        var inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        return new ClaimCheckMessageSerializer(inner, store);
    }

    [Fact]
    public void write_restores_already_cleared_blobs_when_a_later_offload_throws()
    {
        var image = new byte[] { 1, 2, 3, 4 };
        var notes = new string('z', 64);
        var message = new MultiBlobMessage("multi", image, notes);

        // Fail on the second StoreAsync: the first blob is stored + cleared, then the store throws
        // while off-loading the second. Both properties must come back intact.
        var store = new FailOnNthStore(failOn: 2);
        var sut = NewSut(store);
        var envelope = new Envelope(message);

        Should.Throw<InvalidOperationException>(() => sut.Write(envelope));

        store.StoreCalls.ShouldBe(2);
        message.Image.ShouldNotBeNull();
        message.Image!.ShouldBe(image);
        message.Notes.ShouldBe(notes);
    }

    [Fact]
    public async Task write_async_restores_already_cleared_blobs_when_a_later_offload_throws()
    {
        var image = new byte[] { 5, 6, 7, 8 };
        var notes = new string('q', 64);
        var message = new MultiBlobMessage("multi", image, notes);

        var store = new FailOnNthStore(failOn: 2);
        var sut = NewSut(store);
        var envelope = new Envelope(message);

        await Should.ThrowAsync<InvalidOperationException>(async () => await sut.WriteAsync(envelope));

        store.StoreCalls.ShouldBe(2);
        message.Image.ShouldNotBeNull();
        message.Image!.ShouldBe(image);
        message.Notes.ShouldBe(notes);
    }

    private sealed class FailOnNthStore : IClaimCheckStore
    {
        private readonly int _failOn;
        public int StoreCalls { get; private set; }

        public FailOnNthStore(int failOn) => _failOn = failOn;

        public Task<ClaimCheckToken> StoreAsync(ReadOnlyMemory<byte> payload, string contentType,
            CancellationToken cancellationToken = default)
        {
            StoreCalls++;
            if (StoreCalls == _failOn)
            {
                throw new InvalidOperationException("simulated store failure");
            }

            return Task.FromResult(new ClaimCheckToken(Guid.NewGuid().ToString("N"), contentType, payload.Length));
        }

        public Task<ReadOnlyMemory<byte>> LoadAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
