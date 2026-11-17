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

// With DrainWaitForPrefetch on, StopAsync cancels WITHOUT nowait and awaits cancel-ok (plus a batch
// flush in durable micro-batching mode), so prefetched deliveries land in the inbox instead of being
// abandoned to broker redelivery. This asserts durable survival, not synchronous handling: the
// receiver latches mid-drain and defers late arrivals to the durability agent (existing
// DurableReceiver behavior), so the guarantee is "every prefetched delivery persists", not "every
// handler ran before stop returned".
//
// GH-5201: on by default since Wolverine 7.0, so the drain is proven with no call at all, with the explicit
// call, and with the explicit call winning over RestoreV6Defaults().
public enum DrainWaitSetting
{
    // No call at all: the 7.0 default
    Default,
    DrainWaitForPrefetch,
    RestoreV6DefaultsThenDrainWaitForPrefetch,
    DoNotDrainWaitForPrefetch,
    RestoreV6Defaults
}

public class drain_wait_for_prefetch : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;
    private GateFirstDeliveryMapper _mapper = null!;
    private DrainPrefetchTracker _tracker = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task startHostAsync(DrainWaitSetting setting)
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

                // After UseRabbitMq() on purpose: the transport reads the flag when it starts
                if (setting == DrainWaitSetting.RestoreV6DefaultsThenDrainWaitForPrefetch) opts.RestoreV6Defaults();

                opts.PublishMessage<DrainPrefetchMessage>().ToRabbitQueue(_queueName);

                // Durable + the default MaximumMessagesToReceive (100) turns on the micro-batching
                // channel, so this exercises StopAsync's DrainBatchedDeliveriesAsync path, not just
                // the cancel-ok wait.
                var listener = opts.ListenToRabbitQueue(_queueName);
                if (setting != DrainWaitSetting.Default) listener.DrainWaitForPrefetch();

                listener
                    .UseDurableInbox()
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
        if (_host == null) return;
        await _host.StopAsync();
        _host.Dispose();
    }

    // Reverted to a fire-and-forget nowait cancel, this fails: the gated mapper keeps 24 of the 25
    // messages undispatched in the client, and the channel would tear down before they reached
    // HandleBasicDeliverAsync. Persisting all 25 is only possible because StopAsync awaited cancel-ok
    // and drained the batching channel first.
    [Theory]
    [InlineData(DrainWaitSetting.Default)]
    [InlineData(DrainWaitSetting.DrainWaitForPrefetch)]
    [InlineData(DrainWaitSetting.RestoreV6DefaultsThenDrainWaitForPrefetch)]
    public async Task prefetched_durable_batch_is_drained_through_on_stop(DrainWaitSetting setting)
    {
        await startHostAsync(setting);

        const int messageCount = 25;

        var bus = _host.MessageBus();
        for (var i = 0; i < messageCount; i++)
        {
            await bus.PublishAsync(new DrainPrefetchMessage(Guid.NewGuid()));
        }

        // Block on the first delivery in the client's single dispatch thread (ConsumerDispatchConcurrency
        // 1), so nothing dispatches past it. The broker still pushes the other 24 onto the wire while it
        // sits blocked; give them a moment to land so they're prefetched, not still in flight.
        _mapper.Entered.WaitOne(10.Seconds())
            .ShouldBeTrue("the first prefetched delivery never reached the gated mapper");
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{_queueName}"));

        var stopTask = agent.StopAndDrainAsync().AsTask();

        _mapper.Release();

        await stopTask.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        // Every prefetched delivery is captured in the inbox rather than left to broker redelivery.
        // Some may still be "Incoming" rather than "Handled": the receiver latches partway through the
        // drain and defers later arrivals to the recovery sweep (pre-existing shutdown behavior).
        var incoming = await runtime.Storage.Admin.AllIncomingAsync();
        var persistedCount = incoming.Count(x => x.MessageType == typeof(DrainPrefetchMessage).ToMessageTypeName());

        persistedCount.ShouldBe(messageCount);

        // And the ones that beat the shutdown latch ran through the handler, proving drained
        // deliveries reach the pipeline, not just the inbox table.
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
/// Blocks the first incoming delivery's envelope mapping until released. Mapping runs synchronously in
/// HandleBasicDeliverAsync, so at the default ConsumerDispatchConcurrency of 1 this blocks the client's
/// single dispatch thread, keeping every other prefetched delivery undispatched.
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

// GH-5201. Turned off -- with the opt-out, or by RestoreV6Defaults() called after UseRabbitMq() -- the listener
// keeps the 6.x fire-and-forget cancel and stops promptly.
public class drain_wait_for_prefetch_turned_off
{
    [Theory]
    [InlineData(DrainWaitSetting.DoNotDrainWaitForPrefetch)]
    [InlineData(DrainWaitSetting.RestoreV6Defaults)]
    public async Task listener_without_the_drain_wait_still_stops_cleanly_and_promptly(DrainWaitSetting setting)
    {
        var queue = RabbitTesting.NextQueueName();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            if (setting == DrainWaitSetting.RestoreV6Defaults) opts.RestoreV6Defaults();

            var listener = opts.ListenToRabbitQueue(queue);
            if (setting == DrainWaitSetting.DoNotDrainWaitForPrefetch) listener.DoNotDrainWaitForPrefetch();
        });

        var runtime = host.GetRuntime();
        runtime.Options.Transports.GetOrCreate<RabbitMqTransport>().Queues[queue].DrainWaitForPrefetch
            .ShouldBeFalse();

        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{queue}"));

        var sw = Stopwatch.StartNew();
        await agent.StopAndDrainAsync();
        sw.Stop();

        agent.Status.ShouldBe(ListeningStatus.Stopped);
        sw.Elapsed.ShouldBeLessThan(5.Seconds());
    }

    [Fact]
    public async Task the_drain_wait_is_on_by_default()
    {
        var queue = RabbitTesting.NextQueueName();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            opts.ListenToRabbitQueue(queue);
        });

        var runtime = host.GetRuntime();
        runtime.Options.Transports.GetOrCreate<RabbitMqTransport>().Queues[queue].DrainWaitForPrefetch
            .ShouldBeTrue();

        // The wait costs an idle listener nothing noticeable at stop
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{queue}"));

        var sw = Stopwatch.StartNew();
        await agent.StopAndDrainAsync();
        sw.Stop();

        agent.Status.ShouldBe(ListeningStatus.Stopped);
        sw.Elapsed.ShouldBeLessThan(5.Seconds());
    }
}
