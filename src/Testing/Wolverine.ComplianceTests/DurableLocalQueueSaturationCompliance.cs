using System.Collections.Concurrent;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5586. A durable local queue holds at most <see cref="Block{T}.DefaultBoundedCapacity"/> envelopes in memory.
/// Once its handler falls that far behind, a publish to it must not wait for room: the envelope is already in the
/// inbox, so it waits there for a worker or for recovery. Before GH-5586 the flush after a committed transaction, and an
/// ordinary publish, both waited for the queue to take the envelope, so a request stayed open after its business
/// changes had committed.
/// </summary>
/// <remarks>
/// Each store runs the same scenario: one message runs behind a gate while the queue's whole capacity waits behind it,
/// and then something else is published to that queue. Opening the gate has to deliver every message exactly once,
/// including the ones that waited in the inbox.
/// </remarks>
public abstract class DurableLocalQueueSaturationCompliance : IAsyncLifetime
{
    public const string QueueName = "saturated";

    // One envelope executing behind the gate, and the queue's whole in-memory capacity behind it
    public const int FillCount = Block<Envelope>.DefaultBoundedCapacity + 1;

    private static readonly TimeSpan PromptCompletion = 10.Seconds();

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    public IHost Host { get; private set; } = null!;

    protected SaturatedQueueGate Gate { get; } = new();

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // The messages that wait in the inbox are delivered by the durability agent's recovery
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SaturatingLocalMessageHandler))
                    .IncludeType(typeof(CreateTodoBehindAFullQueueHandler));

                opts.Services.AddSingleton(Gate);

                opts.PublishMessage<SaturatingLocalMessage>().ToLocalQueue(QueueName).UseDurableInbox().Sequential();

                configureWolverine(opts);
            }).StartAsync(Token);

        await initialize();
    }

    /// <summary>
    /// Wire up the store, with transactional middleware applied to <see cref="CreateTodoBehindAFullQueue"/>
    /// </summary>
    protected abstract void configureWolverine(WolverineOptions opts);

    protected virtual Task initialize()
    {
        return Task.CompletedTask;
    }

    public abstract Task<Todo?> Load(string id);

    public async ValueTask DisposeAsync()
    {
        // Never leave the handler blocked, or the host cannot drain on the way down
        Gate.Release();

        await Host.StopAsync();
        Host.Dispose();
    }

    private async Task fillTheQueueAsync()
    {
        await Host.MessageBus().InvokeAsync(new CreateTodoBehindAFullQueue(Guid.NewGuid().ToString(), FillCount),
            Token, 2.Minutes());

        await Gate.Started.WaitAsync(30.Seconds(), Token);
    }

    private static async Task shouldCompletePromptlyAsync(Func<Task> publish, string because)
    {
        var publishing = publish();
        var finished = await Task.WhenAny(publishing, Task.Delay(PromptCompletion, Token));
        finished.ShouldBe(publishing, because);
        await publishing;
    }

    [Fact]
    public async Task a_transactional_publish_to_a_full_queue_completes_after_the_commit()
    {
        await fillTheQueueAsync();

        var id = Guid.NewGuid().ToString();
        await shouldCompletePromptlyAsync(
            () => Host.MessageBus().InvokeAsync(new CreateTodoBehindAFullQueue(id, 1), Token, 2.Minutes()),
            "The transaction committed, but the flush afterward is waiting for room in the full local queue");

        (await Load(id)).ShouldNotBeNull();

        Gate.Release();
        await Gate.WaitForAsync(FillCount + 1, 2.Minutes());
    }

    [Fact]
    public async Task a_transactional_publish_of_many_messages_to_a_full_queue_completes_after_the_commit()
    {
        await fillTheQueueAsync();

        var id = Guid.NewGuid().ToString();
        await shouldCompletePromptlyAsync(
            () => Host.MessageBus().InvokeAsync(new CreateTodoBehindAFullQueue(id, 250), Token, 2.Minutes()),
            "The transaction committed, but the flush afterward is waiting for room in the full local queue");

        (await Load(id)).ShouldNotBeNull();

        Gate.Release();
        await Gate.WaitForAsync(FillCount + 250, 2.Minutes());
    }

    [Fact]
    public async Task the_queue_takes_messages_directly_again_once_it_has_drained()
    {
        var runtime = Host.GetRuntime();
        var queue = (DurableLocalQueue)runtime.Endpoints.GetOrBuildSendingAgent(new Uri("local://" + QueueName));

        await fillTheQueueAsync();
        queue.IsFull.ShouldBeTrue();

        Gate.Release();
        await Gate.WaitForAsync(FillCount, 2.Minutes());
        queue.IsFull.ShouldBeFalse();

        var tracked = await Host.TrackActivity().Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new SaturatingLocalMessage(Guid.NewGuid()));

        // Owned by this node and enqueued right away, not left in the inbox for recovery
        tracked.Sent.SingleEnvelope<SaturatingLocalMessage>().OwnerId
            .ShouldBe(runtime.DurabilitySettings.AssignedNodeNumber);
    }

    [Fact]
    public async Task an_ordinary_publish_to_a_full_queue_completes_once_the_message_is_persisted()
    {
        await fillTheQueueAsync();

        await shouldCompletePromptlyAsync(
            () => Host.MessageBus().PublishAsync(new SaturatingLocalMessage(Guid.NewGuid())).AsTask(),
            "The message is in the inbox, but the publish is waiting for room in the full local queue");

        Gate.Release();
        await Gate.WaitForAsync(FillCount + 1, 2.Minutes());
    }
}

public record SaturatingLocalMessage(Guid Id);

public record CreateTodoBehindAFullQueue(string Id, int Messages);

public class SaturatedQueueGate
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, int> _handled = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public void Release() => _gate.TrySetResult();

    public async Task PassAsync(SaturatingLocalMessage message)
    {
        _started.TrySetResult();
        await _gate.Task;
        _handled.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
    }

    public async Task WaitForAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (_handled.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        _handled.Count.ShouldBe(expected, "Every persisted message is delivered");

        // Anything delivered twice would have shown up by now, but give a late duplicate a moment to land
        await Task.Delay(1.Seconds());
        _handled.Where(x => x.Value > 1).Select(x => x.Key).ShouldBeEmpty("No message is delivered twice");
    }
}

[WolverineIgnore]
public static class SaturatingLocalMessageHandler
{
    public static Task Handle(SaturatingLocalMessage message, SaturatedQueueGate gate) => gate.PassAsync(message);
}

[WolverineIgnore]
public static class CreateTodoBehindAFullQueueHandler
{
    public static (Insert<Todo>, OutgoingMessages) Handle(CreateTodoBehindAFullQueue command)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < command.Messages; i++)
        {
            messages.Add(new SaturatingLocalMessage(Guid.NewGuid()));
        }

        return (Storage.Insert(new Todo { Id = command.Id, Name = "Behind a full queue" }), messages);
    }
}
