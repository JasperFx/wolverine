using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// Coverage for RabbitMqListenerConfiguration.DrainWaitForPrefetch(): with the flag on, listener
// StopAsync sends basic.cancel WITHOUT nowait and awaits cancel-ok (plus a batch flush for durable
// micro-batching) before returning, so prefetched-but-unprocessed deliveries are handed to the
// receiver and durably persisted to the inbox instead of being abandoned to broker redelivery.
// Without the flag, StopAsync sends a nowait cancel and returns immediately, and the listener is
// disposed (tearing down the channel) right behind it -- any delivery still sitting in the
// RabbitMQ client's own dispatch buffer at that instant is simply lost and left for the broker to
// requeue. Note this is about persistence, not synchronous handling: once the receiver latches
// mid-drain, any delivery that hasn't yet reached the handler pipeline is persisted and deferred
// for the durability agent to recover rather than executed inline (pre-existing DurableReceiver
// shutdown behavior) -- so the assertion here is "every prefetched delivery survives durably",
// not "every prefetched delivery's handler ran before stop returned".
public class drain_wait_for_prefetch : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;
    private GateFirstDeliveryMapper _mapper = null!;
    private DrainPrefetchTracker _tracker = null!;

    public async ValueTask InitializeAsync()
    {
        _queueName = RabbitTesting.NextQueueName();
        var schemaName = $"drain_prefetch_{Guid.NewGuid():N}";
        _tracker = new DrainPrefetchTracker();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;

                opts.Services.AddSingleton(_tracker);

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = schemaName;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = schemaName);

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<DrainPrefetchMessage>().ToRabbitQueue(_queueName);

                // Durable + the default MaximumMessagesToReceive (100) is exactly the combination
                // that turns on WorkerQueueMessageConsumer's micro-batching channel, so this
                // exercises RabbitMqListener.StopAsync's DrainBatchedDeliveriesAsync call, not just
                // the CancelOkReceived wait.
                opts.ListenToRabbitQueue(_queueName)
                    .UseDurableInbox()
                    .DrainWaitForPrefetch()
                    .PreFetchCount(100)
                    .ConfigureQueue(q =>
                    {
                        var queue = (RabbitMqQueue)q;
                        _mapper = new GateFirstDeliveryMapper(new RabbitMqEnvelopeMapper(queue, null!));
                        queue.EnvelopeMapper = _mapper;
                    });

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // This is the test that would fail if StopAsync reverted to a fire-and-forget nowait cancel:
    // the gated mapper guarantees 24 of the 25 published messages are still undispatched in the
    // RabbitMQ client at the instant StopAndDrainAsync is called. Reverted to fire-and-forget, the
    // channel tears down behind the nowait cancel before the client ever hands those 24 to
    // HandleBasicDeliverAsync, so they'd never even reach the inbox -- durably persisting all 25
    // (rather than only however many happened to race ahead of the cancel) is only possible
    // because StopAsync waited for cancel-ok and drained the batching channel first.
    [Fact]
    public async Task prefetched_durable_batch_is_drained_through_on_stop()
    {
        const int messageCount = 25;

        var bus = _host.MessageBus();
        for (var i = 0; i < messageCount; i++)
        {
            await bus.PublishAsync(new DrainPrefetchMessage(Guid.NewGuid()));
        }

        // Wait until the FIRST delivery is blocking the client's single dispatch thread (default
        // ConsumerDispatchConcurrency is 1) -- this guarantees no delivery has been dispatched
        // past it yet. The broker still routes and pushes the other 24 onto the wire while that
        // first delivery sits blocked, so give that a moment to land before stopping -- otherwise
        // some of the 24 may still be in flight from the broker rather than already prefetched.
        _mapper.Entered.WaitOne(10.Seconds())
            .ShouldBeTrue("the first prefetched delivery never reached the gated mapper");
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{_queueName}"));

        var stopTask = agent.StopAndDrainAsync().AsTask();

        _mapper.Release();

        await stopTask.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        // The durable guarantee this feature makes: every prefetched delivery is captured in the
        // inbox, so none of them silently vanish and rely on the broker to redeliver them. (A
        // handful may still be sitting as "Incoming" rather than "Handled" -- the receiver latches
        // partway through the drain and defers anything that arrives after that point to the
        // durability recovery sweep instead of running it inline; that's pre-existing shutdown
        // behavior, not something this feature changes.)
        var incoming = await runtime.Storage.Admin.AllIncomingAsync();
        var persistedCount = incoming.Count(x => x.MessageType == typeof(DrainPrefetchMessage).ToMessageTypeName());

        persistedCount.ShouldBe(messageCount);

        // And the ones that beat the receiver's shutdown latch ran through the actual handler,
        // proving the drained deliveries reach the pipeline and not just the inbox table.
        _tracker.Handled.Count.ShouldBeGreaterThan(0);
    }
}

public record DrainPrefetchMessage(Guid Id);

public class DrainPrefetchTracker
{
    public readonly ConcurrentBag<Guid> Handled = new();
}

public static class DrainPrefetchMessageHandler
{
    public static void Handle(DrainPrefetchMessage message, DrainPrefetchTracker tracker)
    {
        tracker.Handled.Add(message.Id);
    }
}

/// <summary>
/// Wraps the default mapper and blocks the FIRST incoming delivery's envelope mapping until
/// released. Mapping happens synchronously inside WorkerQueueMessageConsumer.HandleBasicDeliverAsync,
/// so with the default ConsumerDispatchConcurrency of 1 this blocks the RabbitMQ client's single
/// dispatch thread, guaranteeing every other already-prefetched delivery is still sitting
/// undispatched in the client.
/// </summary>
internal class GateFirstDeliveryMapper : IRabbitMqEnvelopeMapper
{
    private readonly IRabbitMqEnvelopeMapper _inner;
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _hits;

    public GateFirstDeliveryMapper(IRabbitMqEnvelopeMapper inner)
    {
        _inner = inner;
    }

    public WaitHandle Entered => _entered.WaitHandle;

    public void Release()
    {
        _release.Set();
    }

    public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)
    {
        if (Interlocked.Increment(ref _hits) == 1)
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(20));
        }

        _inner.MapIncomingToEnvelope(envelope, incoming);
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)
    {
        _inner.MapEnvelopeToOutgoing(envelope, outgoing);
    }
}

// Cheap parity check: a listener that never opts in keeps the old fire-and-forget behavior and
// stops promptly, rather than picking up any waiting behavior by accident.
public class drain_wait_for_prefetch_default_off
{
    [Fact]
    public async Task listener_without_the_flag_still_stops_cleanly_and_promptly()
    {
        var queue = RabbitTesting.NextQueueName();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            opts.ListenToRabbitQueue(queue); // DrainWaitForPrefetch() not called -- default off
        });

        var runtime = host.GetRuntime();
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{queue}"));

        var sw = Stopwatch.StartNew();
        await agent.StopAndDrainAsync();
        sw.Stop();

        agent.Status.ShouldBe(ListeningStatus.Stopped);
        sw.Elapsed.ShouldBeLessThan(5.Seconds());
    }
}
