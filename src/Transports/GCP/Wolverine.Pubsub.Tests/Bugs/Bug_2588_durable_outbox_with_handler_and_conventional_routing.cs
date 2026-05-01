using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.Pubsub.Tests.Bugs;

/// <summary>
/// Locks down GH-2588 for the GCP Pub/Sub conventional routing. Without the
/// structural fix, registering a handler triggers listener discovery which
/// pre-creates the corresponding topic; that endpoint is then compiled during
/// <c>BrokerTransport.InitializeAsync</c> BEFORE any sender subscription is
/// registered, so AllSenders policies like
/// <c>UseDurableOutboxOnAllSendingEndpoints</c> short-circuit on
/// <c>endpoint.Subscriptions.Any() == false</c> and never upgrade the endpoint
/// mode to Durable.
/// </summary>
public class Bug_2588_durable_outbox_with_handler_and_conventional_routing : IAsyncLifetime
{
    private IHost _host = default!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLettering()
                    .EnableSystemEndpoints()
                    .UseConventionalRouting();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.DisableConventionalDiscovery().IncludeType<Bug2588PubsubHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
    }

    [Fact]
    public void conventionally_routed_sender_should_be_durable_when_handler_is_also_registered()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // Pull the conventional broker sender directly from the convention. Local
        // routing wins precedence over MessageRoutingConventions for handled message
        // types, so RoutingFor(...).Routes will surface the local handler queue —
        // not the broker endpoint #2588 is about. We need to verify that the broker
        // sender pre-registered by PreregisterSenders had the AllSenders policy
        // applied during BrokerTransport.InitializeAsync's Compile() call.
        var brokerEndpoint = runtime.Options.RoutingConventions
            .SelectMany(c => c.DiscoverSenders(typeof(Bug2588PubsubMessage), runtime))
            .Single();

        brokerEndpoint.Mode.ShouldBe(EndpointMode.Durable);
    }
}

public class Bug2588PubsubMessage;

public class Bug2588PubsubHandler
{
    public static void Handle(Bug2588PubsubMessage message)
    {
        // no-op
    }
}
