using IntegrationTests;
using JasperFx.Events;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
// The store's own namespace still carries the obsolete IEventForwarding until 8.0, so the core one is aliased
using IEventForwarding = Wolverine.Persistence.EventSourcing.Forwarding.IEventForwarding;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

// GH-5514: event forwarding configured once against the core IEventForwarding, applied to Polecat. The obsolete
// Wolverine.Polecat.IEventForwarding / EventForwardingTransform<T> are still covered by
// subscribe_to_event_requires_completion.
public class core_event_forwarding_5514
{
    // Store-agnostic configuration: nothing here names Polecat
    private static void forwardHappenedAsTranslated(IEventForwarding forwarding)
    {
        forwarding.SubscribeToEvent<PcCoreForwardedHappened>()
            .TransformedTo(e => new PcCoreForwardedTranslated(e.Data.Name));
    }

    private static IHostBuilder hostFor(Action<PolecatIntegration> integration)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "pc_core_forwarding";
                }).IntegrateWithWolverine(integration);

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcCoreForwardedTranslatedHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
            });

    [Fact]
    public void the_integration_is_the_core_event_forwarding_and_hands_back_the_core_transform()
    {
        var integration = new PolecatIntegration();
        integration.ShouldBeAssignableTo<IEventForwarding>();

        integration.SubscribeToEvent<PcCoreForwardedHappened>()
            .ShouldBeAssignableTo<Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform<PcCoreForwardedHappened>>();
    }

    [Fact]
    public async Task a_transformation_configured_through_the_core_interface_is_routed()
    {
        using var host = await hostFor(m => forwardHappenedAsTranslated(m)).StartAsync(TestContext.Current.CancellationToken);

        var routes = host.GetRuntime().RoutingFor(typeof(IEvent<PcCoreForwardedHappened>)).Routes;
        routes.ShouldContain(r => r.GetType().IsGenericType
                                  && r.GetType().GetGenericArguments().Last() == typeof(PcCoreForwardedTranslated));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_bare_subscription_through_the_core_interface_fails_at_bootstrap()
    {
        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            hostFor(m => ((IEventForwarding)m).SubscribeToEvent<PcCoreForwardedHappened>())
                .StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(nameof(PcCoreForwardedHappened));
    }
}

public record PcCoreForwardedHappened(string Name);

public record PcCoreForwardedTranslated(string Name);

public static class PcCoreForwardedTranslatedHandler
{
    public static void Handle(PcCoreForwardedTranslated message)
    {
    }
}
