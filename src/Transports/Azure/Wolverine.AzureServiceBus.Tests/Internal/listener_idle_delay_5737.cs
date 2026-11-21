using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// GH-5737. The buffered/durable Azure Service Bus listener polls with ReceiveMessagesAsync and waited a fixed 250ms
/// after every empty receive -- the same shape as the SQS listener the issue was reported against. Both now read the
/// endpoint's IdleDelay through the one core option.
/// </summary>
public class listener_idle_delay_5737 : IAsyncDisposable
{
    private readonly AzureServiceBusTransport theTransport = new();
    private readonly ServiceBusReceiver theReceiver = Substitute.For<ServiceBusReceiver>();
    private BatchedAzureServiceBusListener? theListener;

    public listener_idle_delay_5737()
    {
        // Every receive comes back empty at once, the way a quiet queue does once MaximumWaitTime runs out
        theReceiver.ReceiveMessagesAsync(Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]));
    }

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }
    }

    [Fact]
    public void configure_the_idle_delay_on_a_queue_listener()
    {
        var queue = theTransport.Queues["quiet"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue).IdleDelay(5.Minutes());
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.IdleDelay.ShouldBe(5.Minutes());
    }

    [Fact]
    public void configure_the_idle_delay_on_a_subscription_listener()
    {
        var subscription = new AzureServiceBusSubscription(theTransport, theTransport.Topics["events"], "quiet");

        var configuration = new AzureServiceBusSubscriptionListenerConfiguration(subscription).IdleDelay(5.Minutes());
        ((IDelayedEndpointConfiguration)configuration).Apply();

        subscription.IdleDelay.ShouldBe(5.Minutes());
    }

    [Fact]
    public void the_idle_delay_is_unset_by_default()
    {
        theTransport.Queues["quiet"].IdleDelay.ShouldBeNull();
    }

    [Fact]
    public async Task a_quiet_queue_is_polled_every_250ms_by_default()
    {
        theListener = buildListener(theTransport.Queues["quiet"]);

        await Task.Delay(1500.Milliseconds(), TestContext.Current.CancellationToken);

        receiveCount().ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task the_listener_waits_the_configured_idle_delay_after_an_empty_receive()
    {
        var queue = theTransport.Queues["quiet"];
        queue.IdleDelay = 1.Hours();
        theListener = buildListener(queue);

        await Task.Delay(1500.Milliseconds(), TestContext.Current.CancellationToken);

        // The first receive comes back empty, and the next one is an hour away
        receiveCount().ShouldBe(1);
    }

    [Fact]
    public async Task a_long_idle_delay_does_not_hold_up_shutting_the_listener_down()
    {
        var queue = theTransport.Queues["quiet"];
        queue.IdleDelay = 1.Hours();
        theListener = buildListener(queue);

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);

        await theListener.StopAsync().AsTask().WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    private int receiveCount()
    {
        return theReceiver.ReceivedCalls()
            .Count(x => x.GetMethodInfo().Name == nameof(ServiceBusReceiver.ReceiveMessagesAsync));
    }

    private BatchedAzureServiceBusListener buildListener(AzureServiceBusEndpoint endpoint)
    {
        return new BatchedAzureServiceBusListener(endpoint, NullLogger.Instance, Substitute.For<IReceiver>(),
            theReceiver, Substitute.For<IAzureServiceBusEnvelopeMapper>(), Substitute.For<ISender>(), 3);
    }
}
