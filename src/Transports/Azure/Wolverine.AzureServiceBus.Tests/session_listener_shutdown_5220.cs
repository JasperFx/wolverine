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
/// GH-5220. The session listener's DisposeAsync() cancelled and then immediately disposed its
/// CancellationTokenSource without waiting for the accept loop, so a broker timeout landing during
/// shutdown sent the loop back to its guard, which read the disposed source's Token and faulted with
/// an unobserved ObjectDisposedException. No broker needed: the ServiceBusClient below hands back an
/// accept the test completes by hand.
/// </summary>
public class session_listener_shutdown_5220
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static AzureServiceBusSessionListener buildListener(ServiceBusClient client)
    {
        var transport = new AzureServiceBusTransport();
        transport.UseServiceBusClient(client);

        var queue = transport.Queues["shutdown-5220"];
        queue.Options.RequiresSession = true;

        return new AzureServiceBusSessionListener(transport, queue, Substitute.For<IReceiver>(),
            Substitute.For<IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>>(), NullLogger.Instance,
            Substitute.For<ISender>());
    }

    [Fact]
    public async Task dispose_waits_for_an_in_flight_accept_and_the_loop_ends_cleanly()
    {
        var client = new ControlledClient(honorCancellation: false);
        var listener = buildListener(client);
        await client.AcceptEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var disposal = listener.DisposeAsync().AsTask();

        // The accept ignores cancellation, so disposal has to still be waiting on the loop
        disposal.IsCompleted.ShouldBeFalse();

        // A broker timeout arrives concurrently with shutdown
        client.AcceptResult.SetException(
            new ServiceBusException("controlled empty-session timeout", ServiceBusFailureReason.ServiceTimeout));

        await disposal.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var loop = listener.AcceptLoops.Single();
        loop.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    [Fact]
    public async Task an_idle_listener_stops_and_disposes_cleanly_and_disposal_is_idempotent()
    {
        var client = new ControlledClient(honorCancellation: true);
        var listener = buildListener(client);
        await client.AcceptEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await listener.StopAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // StopAsync joins the loop, not just cancels it
        listener.AcceptLoops.Single().Status.ShouldBe(TaskStatus.RanToCompletion);

        await listener.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await listener.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await listener.StopAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task cancellation_during_the_backoff_delay_ends_the_loop_without_waiting_it_out()
    {
        var client = new ControlledClient(honorCancellation: false);
        var listener = buildListener(client);
        await client.AcceptEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Every accept from here on fails with a timeout, so the loop spends its time in the back off
        // delays and shutdown lands in one of them (or between them)
        client.AcceptResult.SetException(
            new ServiceBusException("controlled empty-session timeout", ServiceBusFailureReason.ServiceTimeout));

        await listener.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        listener.AcceptLoops.Single().Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    internal sealed class ControlledClient : ServiceBusClient
    {
        private readonly bool _honorCancellation;

        public ControlledClient(bool honorCancellation)
        {
            _honorCancellation = honorCancellation;
        }

        public TaskCompletionSource AcceptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ServiceBusSessionReceiver> AcceptResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string queueName,
            ServiceBusSessionReceiverOptions? options = default, CancellationToken cancellationToken = default)
        {
            AcceptEntered.TrySetResult();
            return _honorCancellation ? AcceptResult.Task.WaitAsync(cancellationToken) : AcceptResult.Task;
        }
    }
}
