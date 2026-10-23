using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
///     PR #5044, unit level with a faked ServiceBusSender (no emulator): what the Azure Service
///     Bus sender protocol reports to the inline send's grouped path (BatchedSender.SendThroughProtocolAsync)
/// </summary>
public class serverless_with_outbox_azure_service_bus_5044
{
    private const string Unmappable = "serverless5044-unmappable";
    private const string TooLarge = "serverless5044-too-large";

    private readonly List<List<string>> _sentBatches = new();
    private readonly Dictionary<ServiceBusMessageBatch, List<ServiceBusMessage>> _stores = new();
    private readonly BatchedSender theSender;

    public serverless_with_outbox_azure_service_bus_5044()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Cancellation.Returns(CancellationToken.None);

        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "serverless5044-queue");

        // Maps an envelope to a message carrying its MessageType as the subject, and throws for the unmappable one
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        mapper.When(x => x.MapEnvelopeToOutgoing(Arg.Any<Envelope>(), Arg.Any<ServiceBusMessage>()))
            .Do(call =>
            {
                var envelope = call.Arg<Envelope>();
                if (envelope.MessageType == Unmappable) throw new InvalidOperationException("cannot map");
                call.Arg<ServiceBusMessage>().Subject = envelope.MessageType;
                call.Arg<ServiceBusMessage>().MessageId = envelope.Id.ToString();
            });

        // A batch refuses the too-large message, whether it is empty or not, like the real one does
        var sender = Substitute.For<ServiceBusSender>();
        sender.CreateMessageBatchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var store = new List<ServiceBusMessage>();
            var batch = ServiceBusModelFactory.ServiceBusMessageBatch(262144, store,
                tryAddCallback: m => m.Subject != TooLarge);
            _stores[batch] = store;
            return new ValueTask<ServiceBusMessageBatch>(batch);
        });
        sender.SendMessagesAsync(Arg.Any<ServiceBusMessageBatch>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // The model factory batch keeps the messages in the list it was given
                _sentBatches.Add(_stores[call.Arg<ServiceBusMessageBatch>()].Select(m => m.Subject).ToList());
                return Task.CompletedTask;
            });

        var protocol = new AzureServiceBusSenderProtocol(runtime, queue, mapper, sender);
        theSender = new BatchedSender(queue, protocol, CancellationToken.None, NullLogger.Instance);
    }

    private static Envelope envelope(string messageType)
    {
        return new Envelope { Id = Guid.NewGuid(), MessageType = messageType, Data = [1, 2, 3] };
    }

    [Fact]
    public async Task happy_path_one_broker_send_for_the_batch()
    {
        var envelopes = new[] { envelope("a"), envelope("b"), envelope("c") };
        var result = new InlineSendResult();

        await theSender.SendThroughProtocolAsync(envelopes, result);

        _sentBatches.Count.ShouldBe(1);
        _sentBatches[0].ShouldBe(["a", "b", "c"]);
        envelopes.ShouldAllBe(e => result.WasAccepted(e));
    }

    [Fact]
    public async Task an_unmappable_envelope_is_reported_as_a_serialization_failure()
    {
        var envelopes = new[] { envelope("a"), envelope(Unmappable), envelope("c") };
        var result = new InlineSendResult();

        await theSender.SendThroughProtocolAsync(envelopes, result);

        _sentBatches.SelectMany(x => x).ShouldBe(["a", "c"]);

        // GH-5055: it is not accepted, and the inline send and the durability pass dead letter it
        result.WasAccepted(envelopes[0]).ShouldBeTrue();
        result.WasAccepted(envelopes[2]).ShouldBeTrue();
        result.WasAccepted(envelopes[1]).ShouldBeFalse();
        result.IsSerializationFailure(envelopes[1], out _).ShouldBeTrue();
    }

    [Fact]
    public async Task a_too_large_message_after_the_first_one_is_reported_accepted_although_it_was_never_sent()
    {
        var envelopes = new[] { envelope("a"), envelope(TooLarge), envelope("c"), envelope("d") };
        var result = new InlineSendResult();

        await theSender.SendThroughProtocolAsync(envelopes, result);

        var sent = _sentBatches.SelectMany(x => x).ToList();
        sent.ShouldNotContain(TooLarge);

        // EXPECTED: what the broker never got is not reported accepted
        result.WasAccepted(envelopes[1]).ShouldBeFalse(
            $"the too-large envelope was never sent (broker sends: {string.Join(" | ", _sentBatches.Select(b => string.Join(",", b)))}) but the protocol reported the whole batch successful");
    }

    [Fact]
    public async Task a_too_large_message_at_the_head_of_the_batch_holds_back_every_message_after_it()
    {
        // As a durability pass sees the rows, oldest first, every time
        var envelopes = new[] { envelope(TooLarge), envelope("b"), envelope("c") };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = new InlineSendResult();
            await theSender.SendThroughProtocolAsync(envelopes, result);
            envelopes.ShouldAllBe(e => !result.WasAccepted(e));
        }

        // Recorded: b and c are never sent while the too-large row stays at the head of the batch
        _sentBatches.ShouldBeEmpty();
    }
}
