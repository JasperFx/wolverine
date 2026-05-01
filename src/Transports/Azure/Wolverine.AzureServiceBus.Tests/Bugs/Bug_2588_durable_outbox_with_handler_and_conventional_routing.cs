using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Bugs;

/// <summary>
/// Locks down GH-2588 for the Azure Service Bus queue-based conventional routing.
/// Without the structural fix, registering a handler triggers listener discovery
/// which pre-creates the corresponding queue/topic; that endpoint is then compiled
/// during <c>BrokerTransport.InitializeAsync</c> BEFORE any sender subscription is
/// registered, so AllSenders policies like <c>UseDurableOutboxOnAllSendingEndpoints</c>
/// short-circuit on <c>endpoint.Subscriptions.Any() == false</c> and never upgrade
/// the endpoint mode to Durable.
/// </summary>
[Trait("Category", "Flaky")]
public class Bug_2588_durable_outbox_with_handler_and_conventional_routing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .UseConventionalRouting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.DisableConventionalDiscovery().IncludeType<Bug2588AsbHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
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
            .SelectMany(c => c.DiscoverSenders(typeof(Bug2588AsbMessage), runtime))
            .Single();

        brokerEndpoint.Mode.ShouldBe(EndpointMode.Durable);
    }
}

/// <summary>
/// Companion to <see cref="Bug_2588_durable_outbox_with_handler_and_conventional_routing"/>
/// but exercising the topic/subscription broadcasting convention rather than the
/// queue-based one. Both inherit from <c>MessageRoutingConvention&lt;,,,&gt;</c>
/// and share the same fix path.
/// </summary>
[Trait("Category", "Flaky")]
public class Bug_2588_durable_outbox_with_handler_and_topic_broadcasting_routing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .UseTopicAndSubscriptionConventionalRouting(x =>
                    {
                        // Keep names short — Azure Service Bus has a 50-char limit
                        x.SubscriptionNameForListener(t => t.Name.ToLowerInvariant());
                        x.TopicNameForListener(t => t.Name.ToLowerInvariant());
                        x.TopicNameForSender(t => t.Name.ToLowerInvariant());
                    })
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.DisableConventionalDiscovery().IncludeType<Bug2588AsbHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
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
            .SelectMany(c => c.DiscoverSenders(typeof(Bug2588AsbMessage), runtime))
            .Single();

        brokerEndpoint.Mode.ShouldBe(EndpointMode.Durable);
    }
}

public class Bug2588AsbMessage;

public class Bug2588AsbHandler
{
    public static void Handle(Bug2588AsbMessage message)
    {
        // no-op
    }
}
