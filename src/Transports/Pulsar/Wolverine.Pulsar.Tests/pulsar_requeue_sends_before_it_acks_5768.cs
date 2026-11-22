using System.Buffers;
using System.Threading.Channels;
using DotPulsar;
using DotPulsar.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-5768: a requeue, a retry-letter move and a dead-letter move put a copy back on a topic. The copy has to be
// accepted by the broker BEFORE the original is acknowledged, or a failed send (or a process exit in between)
// loses the message. Deterministic: the Pulsar client is a substitute, so every send and ack is recorded in order.
public class pulsar_requeue_sends_before_it_acks_5768
{
    private const string Primary = "persistent://public/default/requeue-5768";
    private const string Other = "persistent://public/default/requeue-5768-other";

    [Fact]
    public async Task a_requeue_sends_the_copy_before_it_acks_the_original()
    {
        await using var pulsar = new FakePulsar();
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Primary}", "ack:1"]);
    }

    [Fact]
    public async Task a_failed_send_leaves_the_original_unacknowledged()
    {
        await using var pulsar = new FakePulsar();
        var listener = pulsar.BuildListener();
        pulsar.FailNextSends = 1;

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await Should.ThrowAsync<Exception>(async () => await listener.DeferAsync(envelope));

        pulsar.Calls.ShouldNotContain("ack:1");
    }

    [Fact]
    public async Task a_retry_after_a_failed_ack_does_not_send_a_second_copy()
    {
        await using var pulsar = new FakePulsar();
        var listener = pulsar.BuildListener();
        pulsar.FailNextAcks = 1;

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await Should.ThrowAsync<Exception>(async () => await listener.DeferAsync(envelope));

        // The receiver's retry block runs the defer again
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Primary}", "ack-failed:1", "ack:1"]);
    }

    [Fact]
    public async Task a_dead_letter_move_sends_the_copy_before_it_acks_the_original()
    {
        await using var pulsar = new FakePulsar(e => e.DeadLetterTopic = DeadLetterTopic.DefaultNative);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await listener.MoveToErrorsAsync(envelope, new DivideByZeroException());

        pulsar.Calls.ShouldBe([$"send:{Primary}-DLQ", "ack:1"]);
    }

    [Fact]
    public async Task a_retry_after_a_failed_ack_on_a_dead_letter_move_does_not_send_a_second_copy()
    {
        await using var pulsar = new FakePulsar(e => e.DeadLetterTopic = DeadLetterTopic.DefaultNative);
        var listener = pulsar.BuildListener();
        pulsar.FailNextAcks = 1;

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await Should.ThrowAsync<Exception>(() => listener.MoveToErrorsAsync(envelope, new DivideByZeroException()));
        await listener.MoveToErrorsAsync(envelope, new DivideByZeroException());

        pulsar.Calls.ShouldBe([$"send:{Primary}-DLQ", "ack-failed:1", "ack:1"]);
    }

    [Fact]
    public async Task a_requeue_under_cumulative_acks_does_not_stall_the_watermark()
    {
        await using var pulsar = new FakePulsar(e =>
        {
            e.AckStrategy = PulsarAckStrategy.Cumulative;
            e.SubscriptionType = SubscriptionType.Exclusive;
        });
        var listener = pulsar.BuildListener();

        var first = await pulsar.ReceiveAsync(entryId: 1);
        var second = await pulsar.ReceiveAsync(entryId: 2);

        // The first message is requeued, the second one succeeds. The requeued id has to leave the ack
        // handler's in-flight set, or the cumulative watermark can never move past it again.
        await listener.DeferAsync(first);
        await listener.CompleteAsync(second);

        pulsar.Calls.ShouldContain("ack-cumulative:2");
    }

    [Fact]
    public async Task a_requeue_copy_keeps_the_key_and_ordering_key_it_was_received_with()
    {
        await using var pulsar = new FakePulsar();
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, key: "customer-7", orderingKey: [1, 2, 3]);
        await listener.DeferAsync(envelope);

        var sent = pulsar.Sent.ShouldHaveSingleItem();
        sent.Metadata.Key.ShouldBe("customer-7");
        sent.Metadata.OrderingKey.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task a_dead_letter_copy_keeps_the_key_it_was_received_with()
    {
        await using var pulsar = new FakePulsar(e => e.DeadLetterTopic = DeadLetterTopic.DefaultNative);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, key: "customer-7");
        await listener.MoveToErrorsAsync(envelope, new DivideByZeroException());

        pulsar.Sent.ShouldHaveSingleItem().Metadata.Key.ShouldBe("customer-7");
    }

    [Fact]
    public async Task a_requeue_copy_goes_back_to_the_additional_topic_it_came_from()
    {
        await using var pulsar = new FakePulsar(e => e.AdditionalTopics.Add(Other));
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, topic: Other);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Other}", "ack:1"]);
    }

    [Fact]
    public async Task a_requeue_copy_of_a_partition_message_goes_back_to_the_partitioned_topic()
    {
        await using var pulsar = new FakePulsar(e => e.AdditionalTopics.Add(Other));
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, topic: Other + "-partition-3", partition: 3);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Other}", "ack:1"]);
    }

    [Fact]
    public async Task a_requeue_copy_from_the_primary_topic_of_a_multi_topic_listener_stays_on_it()
    {
        await using var pulsar = new FakePulsar(e => e.AdditionalTopics.Add(Other));
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, topic: Primary);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Primary}", "ack:1"]);
    }

    [Fact]
    public async Task each_requeue_copy_gets_a_sequence_id_of_its_own_under_deduplication()
    {
        await using var pulsar = new FakePulsar(e => e.DeduplicationEnabled = true);
        var listener = pulsar.BuildListener();
        var id = Guid.NewGuid();

        // The original, then the first requeued copy of it coming back: the same envelope id both times
        var first = await pulsar.ReceiveAsync(entryId: 1, envelopeId: id);
        await listener.DeferAsync(first);
        var second = await pulsar.ReceiveAsync(entryId: 2, envelopeId: id);
        await listener.DeferAsync(second);

        pulsar.Sent.Count.ShouldBe(2);
        pulsar.Sent[1].Metadata.SequenceId.ShouldBeGreaterThan(pulsar.Sent[0].Metadata.SequenceId);
    }

    [Fact]
    public async Task native_redelivery_of_a_message_that_was_already_acked_sends_a_copy_instead()
    {
        // A Buffered listener acks every message as it reaches the in-memory queue, so asking Pulsar to
        // redeliver it afterwards does nothing and the message was gone
        await using var pulsar = new FakePulsar(e => e.UseNativeRedelivery = true);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await listener.CompleteAsync(envelope);

        (await listener.TryRequeueAsync(envelope)).ShouldBeTrue();

        pulsar.Calls.ShouldBe(["ack:1", $"send:{Primary}"]);
    }

    [Fact]
    public async Task native_redelivery_of_a_message_that_is_still_unacked_asks_pulsar_to_redeliver_it()
    {
        await using var pulsar = new FakePulsar(e => e.UseNativeRedelivery = true);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe(["redeliver:1"]);
    }

    internal sealed record SentMessage(string Topic, MessageMetadata Metadata);

    internal sealed class FakePulsar : IAsyncDisposable
    {
        private readonly Channel<IMessage<ReadOnlySequence<byte>>> _incoming =
            Channel.CreateUnbounded<IMessage<ReadOnlySequence<byte>>>();

        private readonly Channel<Envelope> _received = Channel.CreateUnbounded<Envelope>();
        private readonly IPulsarClient _client = Substitute.For<IPulsarClient>();
        private readonly PulsarTransport _transport = new();
        private readonly PulsarEndpoint _endpoint;
        private PulsarListener? _listener;

        public FakePulsar(Action<PulsarEndpoint>? configure = null)
        {
            _endpoint = _transport.EndpointFor(Primary);
            _endpoint.Mode = EndpointMode.Inline;
            configure?.Invoke(_endpoint);

            _client.CreateConsumer(Arg.Any<ConsumerOptions<ReadOnlySequence<byte>>>())
                .Returns(_ => buildConsumer());
            _client.CreateProducer(Arg.Any<ProducerOptions<ReadOnlySequence<byte>>>())
                .Returns(ci => buildProducer(ci.Arg<ProducerOptions<ReadOnlySequence<byte>>>().Topic));

            _transport.Client = _client;
        }

        public List<string> Calls { get; } = new();
        public List<SentMessage> Sent { get; } = new();
        public int FailNextSends { get; set; }
        public int FailNextAcks { get; set; }

        public PulsarListener BuildListener()
        {
            var runtime = Substitute.For<IWolverineRuntime>();
            runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
            runtime.Options.Returns(new WolverineOptions { ServiceName = "requeue5768" });

            var receiver = Substitute.For<IReceiver>();
            receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>())
                .Returns(ci =>
                {
                    _received.Writer.TryWrite(ci.Arg<Envelope>());
                    return ValueTask.CompletedTask;
                });

            _listener = new PulsarListener(runtime, _endpoint, receiver, _transport, CancellationToken.None);
            return _listener;
        }

        public async Task<Envelope> ReceiveAsync(ulong entryId, string? key = null, byte[]? orderingKey = null,
            string? topic = null, int partition = -1, Guid? envelopeId = null)
        {
            var properties = new Dictionary<string, string>
            {
                [EnvelopeConstants.IdKey] = (envelopeId ?? Guid.NewGuid()).ToString(),
                [EnvelopeConstants.MessageTypeKey] = "requeue-5768",
                [EnvelopeConstants.ContentTypeKey] = "application/json"
            };

            var message = Substitute.For<IMessage<ReadOnlySequence<byte>>>();
            message.MessageId.Returns(new MessageId(10, entryId, partition, -1, topic ?? ""));
            message.Properties.Returns(properties);
            message.Data.Returns(new ReadOnlySequence<byte>("{}"u8.ToArray()));
            message.HasKey.Returns(key != null);
            message.Key.Returns(key);
            message.HasOrderingKey.Returns(orderingKey != null);
            message.OrderingKey.Returns(orderingKey ?? []);

            await _incoming.Writer.WriteAsync(message);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _received.Reader.ReadAsync(timeout.Token);
        }

        private IConsumer<ReadOnlySequence<byte>> buildConsumer()
        {
            var consumer = Substitute.For<IConsumer<ReadOnlySequence<byte>>>();
            consumer.Topic.Returns(Primary);

            consumer.Receive(Arg.Any<CancellationToken>())
                .Returns(ci => new ValueTask<IMessage<ReadOnlySequence<byte>>>(
                    _incoming.Reader.ReadAsync(ci.Arg<CancellationToken>()).AsTask()));

            consumer.Acknowledge(Arg.Any<MessageId>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var entry = ci.Arg<MessageId>().EntryId;
                    if (FailNextAcks > 0)
                    {
                        FailNextAcks--;
                        Calls.Add($"ack-failed:{entry}");
                        return ValueTask.FromException(new InvalidOperationException("Ack failed"));
                    }

                    Calls.Add($"ack:{entry}");
                    return ValueTask.CompletedTask;
                });

            consumer.AcknowledgeCumulative(Arg.Any<MessageId>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Calls.Add($"ack-cumulative:{ci.Arg<MessageId>().EntryId}");
                    return ValueTask.CompletedTask;
                });

            consumer.RedeliverUnacknowledgedMessages(Arg.Any<IEnumerable<MessageId>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    foreach (var id in ci.Arg<IEnumerable<MessageId>>())
                    {
                        Calls.Add($"redeliver:{id.EntryId}");
                    }

                    return ValueTask.CompletedTask;
                });

            return consumer;
        }

        private IProducer<ReadOnlySequence<byte>> buildProducer(string topic)
        {
            var producer = Substitute.For<IProducer<ReadOnlySequence<byte>>>();
            producer.Topic.Returns(topic);
            producer.Send(Arg.Any<MessageMetadata>(), Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    if (FailNextSends > 0)
                    {
                        FailNextSends--;
                        Calls.Add($"send-failed:{topic}");
                        return ValueTask.FromException<MessageId>(new InvalidOperationException("Send failed"));
                    }

                    Calls.Add($"send:{topic}");
                    Sent.Add(new SentMessage(topic, ci.Arg<MessageMetadata>()));
                    return new ValueTask<MessageId>(MessageId.Earliest);
                });

            return producer;
        }

        public async ValueTask DisposeAsync()
        {
            if (_listener != null)
            {
                await _listener.DisposeAsync();
            }

            _incoming.Writer.TryComplete();
        }
    }
}
