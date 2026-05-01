using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// Companion to <see cref="Bug_2304_conventional_routing_ignores_durable_outbox_policy"/>.
/// That test passes only because it never registers a handler for Bug2304Message —
/// so conventional routing never pre-creates the sender exchange via
/// <c>RabbitMqMessageRoutingConvention.ApplyListenerRoutingDefaults</c>. As soon as
/// a handler IS registered (the realistic case), the exchange gets created during
/// listener discovery and is then compiled by <c>BrokerTransport.InitializeAsync</c>
/// BEFORE any sender subscription is registered — meaning AllSenders policies like
/// <c>UseDurableOutboxOnAllSendingEndpoints</c> short-circuit on
/// <c>endpoint.Subscriptions.Any() == false</c>. Locks down the structural fix
/// in <c>WolverineRuntime.discoverListenersFromConventions</c> /
/// <c>MessageRoutingConvention.PreregisterSenders</c>. See GH-2588.
/// </summary>
public class Bug_2588_durable_outbox_with_handler_and_conventional_routing : IDisposable
{
    private readonly IHost _host;

    public Bug_2588_durable_outbox_with_handler_and_conventional_routing()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine();

            opts.UseRabbitMq()
                .UseConventionalRouting()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            // Critical to reproduce: register the handler so conventional routing's
            // DiscoverListeners pre-creates the sender exchange via
            // ApplyListenerRoutingDefaults BEFORE BrokerTransport.InitializeAsync compiles it.
            opts.DisableConventionalDiscovery().IncludeType<Bug2588Handler>();
        });
    }

    [Fact]
    public void conventionally_routed_sender_should_be_durable_when_handler_is_also_registered()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // Find the conventional broker sender directly via the public Transports
        // collection — looking for the non-local endpoint that has a subscription
        // matching our message type. Local routing wins precedence over
        // MessageRoutingConventions for handled types so we can't go through
        // RoutingFor; we need to inspect the broker endpoint that PreregisterSenders
        // + BrokerTransport.InitializeAsync compiled at startup, which is where the
        // AllSenders policy needed to fire. (Avoids the internal RoutingConventions
        // API so this works in test projects without InternalsVisibleTo coverage.)
        var brokerEndpoint = runtime.Options.Transports
            .AllEndpoints()
            .Single(e => e.Uri.Scheme != "local"
                      && e.Subscriptions.Any(s => s.Matches(typeof(Bug2588Message))));

        brokerEndpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    public void Dispose()
    {
        _host?.Dispose();
    }
}

public class Bug2588Message;

public class Bug2588Handler
{
    public static void Handle(Bug2588Message message)
    {
        // no-op
    }
}
