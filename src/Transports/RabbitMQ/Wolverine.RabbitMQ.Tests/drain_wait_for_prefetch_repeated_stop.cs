using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// Follow-up to GH-3796. DrainWaitForPrefetch completes the consumer's BatchingChannel inside
// RabbitMqListener.StopAsync, but StopAsync is NOT always a terminal shutdown -- RequeueContinuation
// stops the listener inline from the handler pipeline and the background PauseAsync then stops it a
// second time before disposing it, so a durable listener genuinely sees StopAsync twice on one
// consumer.
//
// A JasperFx BatchingChannel tolerates every post-completion call: TriggerBatch, Complete,
// WaitForCompletionAsync and even PostAsync all quietly do nothing rather than throw. That makes the
// second stop harmless but not free (it re-runs the cancel-ok wait against a drained channel), and it
// makes the window between Complete and Dispose's latch actively dangerous: a delivery landing there
// is posted into a completed channel and SILENTLY DISCARDED, never reaching the receiver -- exactly
// the redelivery this feature exists to prevent. DrainBatchedDeliveriesAsync now latches before
// completing, so such a delivery is rejected-with-requeue instead of vanishing.
//
// This test pins the sequence rather than the race: it is a regression guard, and it passes both
// before and after the fix. The behavior it protects -- everything still drains when StopAsync runs
// twice on one consumer -- is what a naive "guard the second Complete" fix would break.
public class drain_wait_for_prefetch_repeated_stop : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;

    public async ValueTask InitializeAsync()
    {
        _queueName = RabbitTesting.NextQueueName();
        var schemaName = $"drain_repeat_{Guid.NewGuid():N}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.LocalRoutingConventionDisabled = true;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = schemaName;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = schemaName);

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<RepeatedStopMessage>().ToRabbitQueue(_queueName);

                // Durable + MaximumMessagesToReceive > 1 is what creates the BatchingChannel, so this
                // is the configuration where the double Complete() is reachable at all.
                opts.ListenToRabbitQueue(_queueName)
                    .UseDurableInbox()
                    .DrainWaitForPrefetch()
                    .PreFetchCount(100);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task stopping_the_same_consumer_twice_still_drains_everything()
    {
        const int messageCount = 10;

        var bus = _host.MessageBus();
        for (var i = 0; i < messageCount; i++)
        {
            await bus.PublishAsync(new RepeatedStopMessage(Guid.NewGuid()));
        }

        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{_queueName}"));

        // Give the broker a moment to push the batch onto the wire so there is something prefetched
        // to drain rather than an empty stop.
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        // The RequeueContinuation shape: an inline stop from the pipeline...
        await ((ListeningAgent)agent).Listener!.StopAsync();

        // ... followed by the background PauseAsync, which stops the SAME consumer again and only
        // then disposes it.
        await agent.StopAndDrainAsync();

        agent.Status.ShouldBe(ListeningStatus.Stopped);

        var incoming = await runtime.Storage.Admin.AllIncomingAsync();
        incoming.Count(x => x.MessageType == typeof(RepeatedStopMessage).ToMessageTypeName())
            .ShouldBe(messageCount);
    }
}

public record RepeatedStopMessage(Guid Id);

public static class RepeatedStopMessageHandler
{
    public static void Handle(RepeatedStopMessage message)
    {
    }
}
