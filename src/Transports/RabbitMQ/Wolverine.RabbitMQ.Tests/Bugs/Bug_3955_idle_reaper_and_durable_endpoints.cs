using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

// Regression test for GH-3955. Reported by @wieslawo.
//
// A durable RabbitMQ queue used ONLY through IMessageBus.EndpointFor(uri) -- no message-type
// subscriptions, not part of a sharded topology -- went permanently silent after one
// SendingAgentIdleTimeout window. Envelopes kept landing in wolverine_outgoing_envelopes and were
// never sent again until the process restarted.
//
// Two independent defects, both of which had to close:
//
// 1. executeIdleSendingAgentCleanup skipped only local queues and AutoStartSendingAgent()
//    endpoints. AutoStartSendingAgent() is UsedInShardedTopology || Subscriptions.Any(), so an
//    endpoint declared with opts.Publish(p => p.ToRabbitQueue(name).UseDurableOutbox()) and no
//    .Message<T>() looked exactly as disposable as the ephemeral control/reply queues GH-1908
//    was actually written for -- even though its sending agent owns the outbox drain for that
//    destination.
//
// 2. Being reaped left the endpoint unusable rather than merely cold. RabbitMqEndpoint cached the
//    sender forever (_sender ??= ...), so the next EndpointFor(uri) built a fresh
//    DurableSendingAgent around the DISPOSED RabbitMqSender. RabbitMqChannelAgent.EnsureInitiated
//    returns immediately once _disposed is set, so SendAsync threw "Channel has not been started
//    for this sender" until the agent latched, and the circuit breaker's PingAsync went through
//    the same no-op and could never heal it. EndpointCollection.RemoveSendingAgentAsync also left
//    Endpoint.Agent pointing at the disposed agent, which DestinationEndpoint and MessageRoute
//    both read.
//
// The second test does not depend on the reaper at all: it reaps by hand, which is both
// deterministic and the honest way to pin defect 2 now that defect 1 keeps the reaper away from
// durable endpoints.
public class Bug_3955_idle_reaper_and_durable_endpoints : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;
    private Uri _uri = null!;

    public async ValueTask InitializeAsync()
    {
        _queueName = "bug3955_" + Guid.NewGuid().ToString("N")[..8];
        _uri = new Uri("rabbitmq://queue/" + _queueName);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Short enough to reap within a test, long enough not to race the sends themselves
                opts.Durability.SendingAgentIdleTimeout = 1.Seconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3955");

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                // Deliberately NO .Message<T>() -- this endpoint is addressed only by Uri, so it has
                // no subscriptions and AutoStartSendingAgent() is false. That is the whole setup.
                opts.Publish(p => p.ToRabbitQueue(_queueName).UseDurableOutbox());

                opts.ListenToRabbitQueue(_queueName);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task sendAndWaitAsync(string name) =>
        _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(c => c.EndpointFor(_uri).SendAsync(new Bug3955Message(name)).AsTask());

    [Fact]
    public async Task durable_endpoint_still_sends_after_an_idle_window()
    {
        await sendAndWaitAsync("first");

        // Two full reaper ticks. Before the fix the second tick removed the sending agent for a
        // DURABLE endpoint and the send below never reached the broker.
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        runtime.Endpoints.ActiveSendingAgents().Select(x => x.Destination)
            .ShouldContain(_uri, "A durable endpoint's sending agent owns the outbox drain for its destination and must not be reaped as idle.");

        await sendAndWaitAsync("second");
    }

    [Fact]
    public async Task a_reaped_endpoint_comes_back_healthy_on_next_use()
    {
        await sendAndWaitAsync("before");

        var runtime = _host.GetRuntime();
        var endpoint = (RabbitMqEndpoint)runtime.Endpoints.EndpointFor(_uri)!;
        var firstSender = endpoint.ResolveSender(runtime);

        // Exactly what the idle cleanup does, minus the waiting
        await ((EndpointCollection)runtime.Endpoints).RemoveSendingAgentAsync(_uri);

        endpoint.Agent.ShouldBeNull("A removed sending agent must not stay reachable through Endpoint.Agent, which DestinationEndpoint and MessageRoute both read.");

        var secondSender = endpoint.ResolveSender(runtime);
        secondSender.ShouldNotBeSameAs(firstSender);
        ((RabbitMqChannelAgent)secondSender).IsDisposed.ShouldBeFalse();

        // ... and the endpoint genuinely works again rather than latching on a dead channel
        await sendAndWaitAsync("after");
    }
}

public record Bug3955Message(string Name);

public static class Bug3955MessageHandler
{
    public static void Handle(Bug3955Message message)
    {
    }
}
