using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// A drained receiver hands every row this node owns at its address back to the cluster with one blanket release,
/// but only after checking that no other receiver at the same address is working. The check and the release were two
/// separate steps. A brand-new receiver whose first claim -- its first inbox write, or a row its listener dequeued --
/// landed between them had that row released underneath it, and another node could run the message alongside it.
/// </summary>
public class a_drain_release_is_exclusive_with_a_new_receivers_first_claim : IAsyncLifetime
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly StubEndpoint theEndpoint = new("release-race", new StubTransport());
    private readonly List<DurableReceiver> _receivers = new();

    private readonly ConcurrentQueue<string> _events = new();
    private readonly TaskCompletionSource _releaseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finishRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stored = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public a_drain_release_is_exclusive_with_a_new_receivers_first_claim()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);

        // The blanket release is the statement in flight: it holds until the test lets it finish
        theRuntime.Storage.Inbox.ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>()).Returns(async _ =>
        {
            _events.Enqueue("release started");
            _releaseStarted.TrySetResult();
            await _finishRelease.Task;
            _events.Enqueue("release finished");
        });

        theRuntime.Storage.Inbox.StoreIncomingAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            _events.Enqueue("stored");
            _stored.TrySetResult();
            return Task.CompletedTask;
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _finishRelease.TrySetResult();
        foreach (var receiver in _receivers) receiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    private DurableReceiver buildReceiver()
    {
        var receiver = new DurableReceiver(theEndpoint, theRuntime, thePipeline);
        _receivers.Add(receiver);
        return receiver;
    }

    [Fact]
    public async Task a_first_receipt_is_not_persisted_while_a_previous_receivers_blanket_release_is_in_flight()
    {
        var previous = buildReceiver();
        var rebuilt = buildReceiver();

        // The previous receiver is idle, and the rebuilt one has not received anything yet: the check passes and
        // the blanket release goes out
        previous.Latch();
        var drain = previous.DrainAsync().AsTask();
        await _releaseStarted.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The rebuilt receiver's very first receipt lands while that release statement is still in flight
        var envelope = ObjectMother.Envelope();
        envelope.Destination = theEndpoint.Uri;
        var receipt = rebuilt.ReceivedAsync(Substitute.For<IListener>(), envelope).AsTask();

        // Bounds the negative case only: a fixed receiver parks its first claim until the release is over
        await Task.WhenAny(_stored.Task, Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken));

        _finishRelease.TrySetResult();
        await drain.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        await receipt.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        await _stored.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var events = _events.ToArray();
        Array.IndexOf(events, "stored").ShouldBeGreaterThan(Array.IndexOf(events, "release finished"),
            "The new receiver persisted its first row while the previous receiver's blanket release of the same address was in flight: " +
            string.Join(", ", events));
    }

    [Fact]
    public async Task a_previous_receiver_does_not_use_the_blanket_release_once_a_new_receiver_is_activated()
    {
        var previous = buildReceiver();
        var rebuilt = buildReceiver();

        // Activated before anything can be claimed for it, the way a listener activates its receiver
        await rebuilt.ActivateAsync();

        previous.Latch();
        _finishRelease.TrySetResult();
        await previous.DrainAsync();

        _events.ShouldNotContain("release started");
    }
}

/// <summary>
/// The other half of the same race. A database-backed listener's dequeue, a durable local queue's own store, the
/// scheduled poller and inbox recovery all claim a row at the address BEFORE the receiver sees it, so the receiver
/// has to count as working there from before its intake can claim anything, not from its first receipt.
/// </summary>
public class a_durable_intake_activates_its_receiver_before_it_can_claim_rows : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(a_durable_intake_activates_its_receiver_before_it_can_claim_rows).Assembly;
                opts.Policies.UseDurableLocalQueues();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_started_durable_listener_counts_as_working_at_its_address_before_any_message_arrives()
    {
        var endpoint = new StubEndpoint("intake-activation", new StubTransport())
        {
            IsListener = true,
            Mode = EndpointMode.Durable
        };
        endpoint.Compile(theRuntime);

        await using var agent = new ListeningAgent(endpoint, theRuntime);
        await agent.StartAsync();

        var field = typeof(ListeningAgent).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var receiver = ((IReceiver)field.GetValue(agent)!).Unwrap().ShouldBeOfType<DurableReceiver>();

        receiver.HoldsWork.ShouldBeTrue(
            "A listening durable receiver did not count as working at its address until its first receipt, so a drained receiver's blanket release could hand back a row its listener had already claimed");
    }

    [Fact]
    public async Task a_resumed_durable_local_queue_counts_as_working_at_its_address_before_any_message_arrives()
    {
        var queue = theRuntime.Endpoints.AgentForLocalQueue("intake-resume").ShouldBeOfType<DurableLocalQueue>();

        await queue.PauseWithDrainAsync(1.Minutes());
        await queue.StartAsync();

        var field = typeof(DurableLocalQueue).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var receiver = field.GetValue(queue).ShouldBeOfType<DurableReceiver>();

        receiver.HoldsWork.ShouldBeTrue(
            "A resumed durable local queue's receiver did not count as working at its address until its first receipt");
    }
}
