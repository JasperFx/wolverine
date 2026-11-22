using System.Buffers;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DotPulsar;
using DotPulsar.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-5813, deterministic: the Pulsar client is a substitute, so every send, ack and redelivery is recorded in order.
//
// 1. A listener over several topics (Topics() / TopicsPattern()) requeued a message that came back from its
//    retry-letter topic to its primary topic, not to the topic the message was first consumed from.
// 2. A Buffered listener acks each message from its receiver's ack block. A handler that failed before that ack
//    ran had the message natively redelivered and then acked: Pulsar either redelivered a message that was about
//    to be acked, or took the ack first and never redelivered it at all.
public class pulsar_retry_letter_source_topic_and_buffered_race_5813
{
    private const string Primary = "persistent://public/default/retry-source-5813";
    private const string Other = "persistent://public/default/retry-source-5813-other";
    private const string Retry = Primary + "-RETRY";

    private static void multiTopicWithRetryLetters(PulsarEndpoint e)
    {
        e.AdditionalTopics.Add(Other);
        e.SubscriptionType = SubscriptionType.Shared;
        e.RetryLetterTopic = new RetryLetterTopic([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
    }

    [Fact]
    public async Task a_retry_letter_move_carries_the_topic_the_message_was_consumed_from()
    {
        await using var pulsar = new FakePulsar5813(multiTopicWithRetryLetters);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, topic: Other);
        envelope.Attempts = 1;
        await listener.MoveToScheduledUntilAsync(envelope, DateTimeOffset.UtcNow.AddSeconds(1));

        var sent = pulsar.Sent.ShouldHaveSingleItem();
        sent.Topic.ShouldBe(Retry);
        sent.Metadata[PulsarEnvelopeConstants.SourceTopicMetadataKey].ShouldBe(Other);
    }

    [Fact]
    public async Task a_retry_letter_message_is_requeued_to_the_topic_it_was_first_consumed_from()
    {
        await using var pulsar = new FakePulsar5813(multiTopicWithRetryLetters);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveFromRetryAsync(entryId: 1, sourceTopic: Other);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Other}", "ack-retry:1"]);
    }

    [Fact]
    public async Task a_retry_letter_message_from_a_partition_is_requeued_to_the_partitioned_topic()
    {
        await using var pulsar = new FakePulsar5813(multiTopicWithRetryLetters);
        var listener = pulsar.BuildListener();

        var original = await pulsar.ReceiveAsync(entryId: 1, topic: Other + "-partition-2", partition: 2);
        original.Attempts = 1;
        await listener.MoveToScheduledUntilAsync(original, DateTimeOffset.UtcNow.AddSeconds(1));

        pulsar.Sent.ShouldHaveSingleItem().Metadata[PulsarEnvelopeConstants.SourceTopicMetadataKey].ShouldBe(Other);
    }

    [Fact]
    public async Task a_retry_letter_message_moved_again_keeps_its_source_topic()
    {
        await using var pulsar = new FakePulsar5813(multiTopicWithRetryLetters);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveFromRetryAsync(entryId: 1, sourceTopic: Other);
        envelope.Attempts = 2;
        await listener.MoveToScheduledUntilAsync(envelope, DateTimeOffset.UtcNow.AddSeconds(1));

        pulsar.Sent.ShouldHaveSingleItem().Metadata[PulsarEnvelopeConstants.SourceTopicMetadataKey].ShouldBe(Other);
    }

    [Fact]
    public async Task a_retry_letter_message_without_a_source_topic_is_requeued_to_the_primary_topic()
    {
        await using var pulsar = new FakePulsar5813(multiTopicWithRetryLetters);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveFromRetryAsync(entryId: 1, sourceTopic: null);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Primary}", "ack-retry:1"]);
    }

    [Fact]
    public async Task a_source_topic_the_listener_does_not_consume_is_ignored()
    {
        await using var pulsar = new FakePulsar5813(multiTopicWithRetryLetters);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveFromRetryAsync(entryId: 1,
            sourceTopic: "persistent://public/default/somewhere-else-5813");
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Primary}", "ack-retry:1"]);
    }

    [Fact]
    public async Task a_retry_letter_message_of_a_pattern_listener_is_requeued_to_its_source_topic()
    {
        await using var pulsar = new FakePulsar5813(e =>
        {
            e.TopicsPattern = new Regex("persistent://public/default/retry-source-5813.*");
            e.SubscriptionType = SubscriptionType.Shared;
            e.RetryLetterTopic = new RetryLetterTopic([TimeSpan.FromSeconds(1)]);
        });
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveFromRetryAsync(entryId: 1, sourceTopic: Other);
        await listener.DeferAsync(envelope);

        pulsar.Calls.ShouldBe([$"send:{Other}", "ack-retry:1"]);
    }

    [Fact]
    public async Task a_single_topic_listener_does_not_stamp_a_source_topic()
    {
        await using var pulsar = new FakePulsar5813(e =>
        {
            e.SubscriptionType = SubscriptionType.Shared;
            e.RetryLetterTopic = new RetryLetterTopic([TimeSpan.FromSeconds(1)]);
        });
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1, topic: Primary);
        envelope.Attempts = 1;
        await listener.MoveToScheduledUntilAsync(envelope, DateTimeOffset.UtcNow.AddSeconds(1));

        pulsar.Sent.ShouldHaveSingleItem().Metadata[PulsarEnvelopeConstants.SourceTopicMetadataKey].ShouldBeNull();
    }

    [Fact]
    public async Task a_native_redelivery_ahead_of_the_receivers_ack_is_not_acked_afterwards()
    {
        await using var pulsar = new FakePulsar5813(e => e.UseNativeRedelivery = true);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);

        // The Buffered receiver: the handler fails and requeues before the ack block got to the message
        (await listener.TryRequeueAsync(envelope)).ShouldBeTrue();
        await listener.CompleteAsync(envelope);

        pulsar.Calls.ShouldBe(["redeliver:1"]);
    }

    [Fact]
    public async Task a_native_redelivery_through_defer_ahead_of_the_receivers_ack_is_not_acked_afterwards()
    {
        await using var pulsar = new FakePulsar5813(e => e.UseNativeRedelivery = true);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);

        // PulsarNativeResiliencyContinuation calls DeferAsync directly
        await listener.DeferAsync(envelope);
        await listener.CompleteAsync(envelope);

        pulsar.Calls.ShouldBe(["redeliver:1"]);
    }

    [Fact]
    public async Task a_failure_while_the_receivers_ack_is_in_flight_sends_a_copy_instead_of_a_native_redelivery()
    {
        await using var pulsar = new FakePulsar5813(e => e.UseNativeRedelivery = true);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);

        var gate = pulsar.HoldNextAck();
        var ack = listener.CompleteAsync(envelope).AsTask();
        await pulsar.AckStarted;

        // The ack is on its way to the broker: a native redelivery now races it
        (await listener.TryRequeueAsync(envelope)).ShouldBeTrue();

        gate.SetResult();
        await ack;

        pulsar.Calls.ShouldBe([$"send:{Primary}", "ack:1"]);
    }

    [Fact]
    public async Task a_retried_ack_after_a_native_redelivery_still_does_not_ack()
    {
        await using var pulsar = new FakePulsar5813(e => e.UseNativeRedelivery = true);
        var listener = pulsar.BuildListener();

        var envelope = await pulsar.ReceiveAsync(entryId: 1);
        (await listener.TryRequeueAsync(envelope)).ShouldBeTrue();

        // The ack block retries
        await listener.CompleteAsync(envelope);
        await listener.CompleteAsync(envelope);

        pulsar.Calls.ShouldBe(["redeliver:1"]);
    }

    internal sealed record SentMessage(string Topic, MessageMetadata Metadata);

    internal sealed class FakePulsar5813 : IAsyncDisposable
    {
        private readonly Channel<IMessage<ReadOnlySequence<byte>>> _incoming =
            Channel.CreateUnbounded<IMessage<ReadOnlySequence<byte>>>();

        private readonly Channel<IMessage<ReadOnlySequence<byte>>> _incomingRetry =
            Channel.CreateUnbounded<IMessage<ReadOnlySequence<byte>>>();

        private readonly Channel<Envelope> _received = Channel.CreateUnbounded<Envelope>();
        private readonly IPulsarClient _client = Substitute.For<IPulsarClient>();
        private readonly PulsarTransport _transport = new();
        private readonly PulsarEndpoint _endpoint;
        private readonly TaskCompletionSource _ackStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _ackGate;
        private PulsarListener? _listener;

        public FakePulsar5813(Action<PulsarEndpoint>? configure = null)
        {
            _endpoint = _transport.EndpointFor(Primary);
            _endpoint.Mode = EndpointMode.Inline;
            configure?.Invoke(_endpoint);

            _client.CreateConsumer(Arg.Any<ConsumerOptions<ReadOnlySequence<byte>>>())
                .Returns(ci =>
                {
                    var options = ci.Arg<ConsumerOptions<ReadOnlySequence<byte>>>();
                    return options.Topic == Retry
                        ? buildConsumer(Retry, _incomingRetry, "-retry")
                        : buildConsumer(Primary, _incoming, "");
                });
            _client.CreateProducer(Arg.Any<ProducerOptions<ReadOnlySequence<byte>>>())
                .Returns(ci => buildProducer(ci.Arg<ProducerOptions<ReadOnlySequence<byte>>>().Topic));

            _transport.Client = _client;
        }

        public List<string> Calls { get; } = new();
        public List<SentMessage> Sent { get; } = new();
        public Task AckStarted => _ackStarted.Task;

        public TaskCompletionSource HoldNextAck()
        {
            _ackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _ackGate;
        }

        public PulsarListener BuildListener()
        {
            var runtime = Substitute.For<IWolverineRuntime>();
            runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
            runtime.Options.Returns(new WolverineOptions { ServiceName = "retrysource5813" });

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

        public Task<Envelope> ReceiveAsync(ulong entryId, string? topic = null, int partition = -1)
        {
            return receiveAsync(_incoming, entryId, topic ?? Primary, partition, new Dictionary<string, string>());
        }

        public Task<Envelope> ReceiveFromRetryAsync(ulong entryId, string? sourceTopic)
        {
            var properties = new Dictionary<string, string>
            {
                [PulsarEnvelopeConstants.ReconsumeTimes] = "1"
            };

            if (sourceTopic != null)
            {
                properties[PulsarEnvelopeConstants.SourceTopicMetadataKey] = sourceTopic;
            }

            return receiveAsync(_incomingRetry, entryId, Retry, -1, properties);
        }

        private async Task<Envelope> receiveAsync(Channel<IMessage<ReadOnlySequence<byte>>> channel, ulong entryId,
            string topic, int partition, Dictionary<string, string> properties)
        {
            properties[EnvelopeConstants.IdKey] = Guid.NewGuid().ToString();
            properties[EnvelopeConstants.MessageTypeKey] = "retry-source-5813";
            properties[EnvelopeConstants.ContentTypeKey] = "application/json";

            var message = Substitute.For<IMessage<ReadOnlySequence<byte>>>();
            message.MessageId.Returns(new MessageId(10, entryId, partition, -1, topic));
            message.Properties.Returns(properties);
            message.Data.Returns(new ReadOnlySequence<byte>("{}"u8.ToArray()));

            await channel.Writer.WriteAsync(message);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _received.Reader.ReadAsync(timeout.Token);
        }

        private IConsumer<ReadOnlySequence<byte>> buildConsumer(string topic,
            Channel<IMessage<ReadOnlySequence<byte>>> incoming, string label)
        {
            var consumer = Substitute.For<IConsumer<ReadOnlySequence<byte>>>();
            consumer.Topic.Returns(topic);

            consumer.Receive(Arg.Any<CancellationToken>())
                .Returns(ci => new ValueTask<IMessage<ReadOnlySequence<byte>>>(
                    incoming.Reader.ReadAsync(ci.Arg<CancellationToken>()).AsTask()));

            consumer.Acknowledge(Arg.Any<MessageId>(), Arg.Any<CancellationToken>())
                .Returns(ci => new ValueTask(acknowledgeAsync(ci.Arg<MessageId>().EntryId, label)));

            consumer.RedeliverUnacknowledgedMessages(Arg.Any<IEnumerable<MessageId>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    foreach (var id in ci.Arg<IEnumerable<MessageId>>())
                    {
                        Calls.Add($"redeliver{label}:{id.EntryId}");
                    }

                    return ValueTask.CompletedTask;
                });

            return consumer;
        }

        private async Task acknowledgeAsync(ulong entryId, string label)
        {
            var gate = _ackGate;
            _ackGate = null;
            _ackStarted.TrySetResult();

            if (gate != null)
            {
                await gate.Task;
            }

            Calls.Add($"ack{label}:{entryId}");
        }

        private IProducer<ReadOnlySequence<byte>> buildProducer(string topic)
        {
            var producer = Substitute.For<IProducer<ReadOnlySequence<byte>>>();
            producer.Topic.Returns(topic);
            producer.Send(Arg.Any<MessageMetadata>(), Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
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
            _incomingRetry.Writer.TryComplete();
        }
    }
}
