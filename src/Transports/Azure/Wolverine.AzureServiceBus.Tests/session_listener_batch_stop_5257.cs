using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5257. Once the session listener is stopping, the rest of a received batch must not be started: those
/// messages stay unsettled, so the broker redelivers them -- in session order -- after the session lock is
/// released. No broker needed: the session receiver is a double that hands back one batch.
/// </summary>
public class session_listener_batch_stop_5257
{
    [Fact]
    public async Task the_rest_of_a_batch_is_not_started_once_the_listener_is_stopping()
    {
        using var stopping = new CancellationTokenSource();

        var sessionReceiver = Substitute.For<ServiceBusSessionReceiver>();
        sessionReceiver.ReceiveMessagesAsync(Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "one"),
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "two"),
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "three")
            });

        // The stop arrives while the first message is being handled
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>())
            .Returns(_ => new ValueTask(stopping.CancelAsync()));

        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["batch-stop-5257"];
        queue.Options.RequiresSession = true;

        var listener = new SessionSpecificListener(sessionReceiver, queue, receiver,
            Substitute.For<IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>>(), NullLogger.Instance,
            Substitute.For<ISender>());

        await listener.ExecuteAsync(stopping.Token);

        await receiver.Received(1).ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>());

        // Nothing was settled on the way out, so the unstarted messages go back to the broker with the session
        var settlements = sessionReceiver.ReceivedCalls().Select(x => x.GetMethodInfo().Name).ToArray();
        settlements.ShouldNotContain(nameof(ServiceBusSessionReceiver.CompleteMessageAsync));
        settlements.ShouldNotContain(nameof(ServiceBusSessionReceiver.DeadLetterMessageAsync));
        settlements.ShouldNotContain(nameof(ServiceBusSessionReceiver.AbandonMessageAsync));
    }

    [Fact]
    public async Task every_message_in_a_batch_is_received_while_the_listener_is_running()
    {
        var sessionReceiver = Substitute.For<ServiceBusSessionReceiver>();
        sessionReceiver.ReceiveMessagesAsync(Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "one"),
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "two"),
                ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "three")
            });

        var receiver = Substitute.For<IReceiver>();

        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["batch-run-5257"];
        queue.Options.RequiresSession = true;

        var listener = new SessionSpecificListener(sessionReceiver, queue, receiver,
            Substitute.For<IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>>(), NullLogger.Instance,
            Substitute.For<ISender>());

        (await listener.ExecuteAsync(TestContext.Current.CancellationToken)).ShouldBe(3);

        await receiver.Received(3).ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>());
    }
}
