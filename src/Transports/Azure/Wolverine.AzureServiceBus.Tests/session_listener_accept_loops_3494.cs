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
/// GH-3494 (AO2). ListeningAgent already builds Endpoint.ListenerCount listeners, so the session
/// listener spawning ListenerCount accept loops of its own made RequireSessions(n) open n-squared
/// concurrent AcceptNextSessionAsync loops -- 25 of them for RequireSessions(5).
/// </summary>
public class session_listener_accept_loops_3494
{
    private static AzureServiceBusSessionListener buildListener(int listenerCount)
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.Options.RequiresSession = true;
        queue.ListenerCount = listenerCount;

        // The accept loop itself immediately fails against a transport with no live connection and
        // backs off inside its own catch, which is all this test needs -- it only asserts on how
        // many loops were started.
        return new AzureServiceBusSessionListener(transport, queue, Substitute.For<IReceiver>(),
            Substitute.For<IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>>(), NullLogger.Instance,
            Substitute.For<ISender>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task always_runs_exactly_one_accept_loop_per_listener(int listenerCount)
    {
        await using var listener = buildListener(listenerCount);
        listener.AcceptLoopCount.ShouldBe(1);
    }
}
