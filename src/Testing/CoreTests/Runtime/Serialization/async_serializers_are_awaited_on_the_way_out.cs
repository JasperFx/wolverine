using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Runtime.Serialization;

/// <summary>
/// An <see cref="IAsyncMessageSerializer" /> (a schema registry, encryption) can only serialize without blocking
/// when the outgoing paths await <see cref="IAsyncMessageSerializer.WriteAsync" /> before anything reads
/// <see cref="Envelope.Data" />, because reading Data serializes synchronously. The serializer here throws from
/// its synchronous Write, so any path that still reaches it fails.
/// </summary>
public class async_serializers_are_awaited_on_the_way_out
{
    private readonly AsyncOnlySerializer theSerializer = new();

    private Envelope outgoing()
    {
        return new Envelope(new AsyncSerializedMessage("hello"))
        {
            Serializer = theSerializer,
            ContentType = theSerializer.ContentType,
            Destination = new Uri("stub://one")
        };
    }

    [Fact]
    public async Task serializing_ahead_uses_the_async_serializer()
    {
        var envelope = outgoing();

        await envelope.SerializeAheadAsync();

        theSerializer.AsyncWrites.ShouldBe(1);
        envelope.Data.ShouldBe(AsyncOnlySerializer.Bytes);
    }

    [Fact]
    public async Task serializing_ahead_leaves_a_synchronous_serializer_alone()
    {
        var serializer = Substitute.For<IMessageSerializer>();
        var envelope = new Envelope(new AsyncSerializedMessage("hello")) { Serializer = serializer };

        await envelope.SerializeAheadAsync();

        serializer.DidNotReceive().Write(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task get_data_async_returns_a_pooled_body_without_a_message()
    {
        var body = new byte[Envelope.PooledBodyThreshold];
        body[0] = 7;

        var envelope = new Envelope();
        envelope.CopyBodyFrom(body);

        var data = await envelope.GetDataAsync();

        data.ShouldNotBeNull();
        data.Length.ShouldBe(body.Length);
        data[0].ShouldBe((byte)7);
    }

    [Fact]
    public async Task a_transactional_persist_serializes_before_the_store_reads_the_data()
    {
        int? payloadSizeWhenStored = null;
        var transaction = Substitute.For<IEnvelopeTransaction>();
        transaction.PersistOutgoingAsync(Arg.Any<Envelope>()).Returns(ci =>
        {
            payloadSizeWhenStored = ci.Arg<Envelope>().MessagePayloadSize;
            return Task.CompletedTask;
        });

        await transaction.PersistAsync(outgoing());

        payloadSizeWhenStored.ShouldBe(AsyncOnlySerializer.Bytes.Length);
        theSerializer.AsyncWrites.ShouldBe(1);
    }

    [Fact]
    public async Task a_durable_store_serializes_before_the_outbox_reads_the_data()
    {
        int? payloadSizeWhenStored = null;
        var outbox = Substitute.For<IMessageOutbox>();
        outbox.StoreOutgoingAsync(Arg.Any<Envelope>(), Arg.Any<int>()).Returns(ci =>
        {
            payloadSizeWhenStored = ci.Arg<Envelope>().MessagePayloadSize;
            return Task.CompletedTask;
        });

        var sender = Substitute.For<ISender>();
        sender.Destination.Returns(new Uri("stub://one"));

        await using var agent = new DurableSendingAgent(sender, new DurabilitySettings(), NullLogger.Instance,
            Substitute.For<IMessageTracker>(), outbox, new StubEndpoint(new Uri("stub://one")));

        (await agent.TryStoreOutgoingAsync(outgoing())).ShouldBeTrue();

        payloadSizeWhenStored.ShouldBe(AsyncOnlySerializer.Bytes.Length);
        theSerializer.AsyncWrites.ShouldBe(1);
    }

    [Fact]
    public async Task the_batched_sender_serializes_through_the_async_serializer()
    {
        var sent = new TaskCompletionSource<OutgoingMessageBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = Substitute.For<ISenderProtocol>();
        protocol.SendBatchAsync(Arg.Any<ISenderCallback>(), Arg.Any<OutgoingMessageBatch>()).Returns(ci =>
        {
            sent.TrySetResult(ci.Arg<OutgoingMessageBatch>());
            return Task.CompletedTask;
        });

        var endpoint = new TcpEndpoint(2261) { MessageBatchTimeout = 50.Milliseconds() };
        await using var sender = new BatchedSender(endpoint, protocol, CancellationToken.None, NullLogger.Instance);
        sender.RegisterCallback(Substitute.For<ISenderCallback>());

        await sender.SendAsync(outgoing());

        var batch = await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        batch.Messages.Single().Data.ShouldBe(AsyncOnlySerializer.Bytes);
        theSerializer.AsyncWrites.ShouldBe(1);
    }

    [Fact]
    public async Task an_inline_send_through_the_protocol_serializes_through_the_async_serializer()
    {
        var protocol = Substitute.For<ISenderProtocol>();
        protocol.SendBatchAsync(Arg.Any<ISenderCallback>(), Arg.Any<OutgoingMessageBatch>())
            .Returns(ci => ci.Arg<ISenderCallback>().MarkSuccessfulAsync(ci.Arg<OutgoingMessageBatch>()));

        await using var sender = new BatchedSender(new TcpEndpoint(2262), protocol, CancellationToken.None,
            NullLogger.Instance);

        var envelope = outgoing();
        await sender.SendThroughProtocolAsync(envelope);

        envelope.Data.ShouldBe(AsyncOnlySerializer.Bytes);
        theSerializer.AsyncWrites.ShouldBe(1);
    }

    [Fact]
    public async Task an_inline_batch_through_the_protocol_serializes_through_the_async_serializer()
    {
        var protocol = Substitute.For<ISenderProtocol>();
        protocol.SendBatchAsync(Arg.Any<ISenderCallback>(), Arg.Any<OutgoingMessageBatch>())
            .Returns(ci => ci.Arg<ISenderCallback>().MarkSuccessfulAsync(ci.Arg<OutgoingMessageBatch>()));

        await using var sender = new BatchedSender(new TcpEndpoint(2263), protocol, CancellationToken.None,
            NullLogger.Instance);

        var envelopes = new[] { outgoing(), outgoing() };
        var result = new InlineSendResult();
        await sender.SendThroughProtocolAsync(envelopes, result);

        envelopes.ShouldAllBe(x => x.Data != null && x.Data.Length == AsyncOnlySerializer.Bytes.Length);
        theSerializer.AsyncWrites.ShouldBe(2);
    }

    public record AsyncSerializedMessage(string Name);

    private class AsyncOnlySerializer : IAsyncMessageSerializer
    {
        public static readonly byte[] Bytes = [1, 2, 3, 4];

        private int _asyncWrites;
        public int AsyncWrites => _asyncWrites;

        public string ContentType => "application/x-async-only";

        public byte[] Write(Envelope envelope)
            => throw new InvalidOperationException("The synchronous Write blocked on an async serializer");

        public async ValueTask<byte[]> WriteAsync(Envelope envelope)
        {
            // Never completes synchronously, so the paths that await it are really exercised
            await Task.Yield();
            Interlocked.Increment(ref _asyncWrites);
            return Bytes;
        }

        public object ReadFromData(Type messageType, Envelope envelope) => throw new NotSupportedException();

        public object ReadFromData(byte[] data) => throw new NotSupportedException();

        public byte[] WriteMessage(object message) => throw new NotSupportedException();

        public ValueTask<object?> ReadFromDataAsync(Type messageType, Envelope envelope)
            => throw new NotSupportedException();
    }

    private class StubEndpoint : Endpoint
    {
        public StubEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

        protected override bool supportsMode(EndpointMode mode) => true;
    }
}
