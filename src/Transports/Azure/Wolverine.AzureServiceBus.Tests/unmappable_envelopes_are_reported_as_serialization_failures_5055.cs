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
///     GH-5055, unit level with a faked ServiceBusSender (no emulator). An envelope the outgoing mapper throws on can
///     never be sent, so the protocol reports it through MarkSerializationFailureAsync, which dead letters it, and
///     only reports the envelopes it really sent as successful. It used to log the envelope as discarded and then
///     report the whole original batch as sent, so its outbox row was deleted with nothing but a log line left
/// </summary>
public class unmappable_envelopes_are_reported_as_serialization_failures_5055
{
    private const string Unmappable = "gh5055-unmappable";

    private readonly ISenderCallback theCallback = Substitute.For<ISenderCallback>();
    private readonly List<string> _sent = new();
    private readonly AzureServiceBusQueue theQueue = new(new AzureServiceBusTransport(), "gh5055-queue");
    private readonly AzureServiceBusSenderProtocol theProtocol;

    public unmappable_envelopes_are_reported_as_serialization_failures_5055()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Cancellation.Returns(CancellationToken.None);

        // A user's mapper that cannot handle one of the messages
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        mapper.When(x => x.MapEnvelopeToOutgoing(Arg.Any<Envelope>(), Arg.Any<ServiceBusMessage>()))
            .Do(call =>
            {
                var envelope = call.Arg<Envelope>();
                if (envelope.MessageType == Unmappable) throw new InvalidOperationException("cannot map");
                call.Arg<ServiceBusMessage>().Subject = envelope.MessageType;
                call.Arg<ServiceBusMessage>().SessionId = "one";
            });

        var stores = new Dictionary<ServiceBusMessageBatch, List<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        sender.CreateMessageBatchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var store = new List<ServiceBusMessage>();
            var batch = ServiceBusModelFactory.ServiceBusMessageBatch(262144, store);
            stores[batch] = store;
            return new ValueTask<ServiceBusMessageBatch>(batch);
        });
        sender.SendMessagesAsync(Arg.Any<ServiceBusMessageBatch>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sent.AddRange(stores[call.Arg<ServiceBusMessageBatch>()].Select(m => m.Subject));
                return Task.CompletedTask;
            });

        theProtocol = new AzureServiceBusSenderProtocol(runtime, theQueue, mapper, sender);
    }

    private static Envelope envelope(string messageType)
    {
        return new Envelope { Id = Guid.NewGuid(), MessageType = messageType, Data = [1, 2, 3] };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task two_of_three_are_sent_and_the_unmappable_one_is_a_serialization_failure(bool partitioned)
    {
        theQueue.Options.EnablePartitioning = partitioned;

        var one = envelope("one");
        var unmappable = envelope(Unmappable);
        var three = envelope("three");

        await theProtocol.SendBatchAsync(theCallback,
            new OutgoingMessageBatch(theQueue.Uri, [one, unmappable, three]));

        _sent.ShouldBe(["one", "three"]);

        await theCallback.Received(1).MarkSerializationFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 1 && b.Messages[0] == unmappable));

        await theCallback.Received(1).MarkSuccessfulAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 2 && b.Messages.Contains(one) && b.Messages.Contains(three)));
        await theCallback.DidNotReceive().MarkSuccessfulAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Contains(unmappable)));
        await theCallback.DidNotReceiveWithAnyArgs().MarkProcessingFailureAsync(default!, default);
    }

    [Fact]
    public async Task a_batch_with_nothing_mappable_sends_nothing_and_reports_nothing_successful()
    {
        var unmappable = envelope(Unmappable);

        await theProtocol.SendBatchAsync(theCallback, new OutgoingMessageBatch(theQueue.Uri, [unmappable]));

        _sent.ShouldBeEmpty();
        await theCallback.Received(1).MarkSerializationFailureAsync(Arg.Any<OutgoingMessageBatch>());
        await theCallback.DidNotReceiveWithAnyArgs().MarkSuccessfulAsync(default(OutgoingMessageBatch)!);
    }
}
