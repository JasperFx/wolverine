using Fisher;
using JasperFx;
using JasperFx.Events;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace FisherTests;

// GH-5514: event forwarding configured once against the core IEventForwardingConfiguration, applied to Fisher. The obsolete
// Wolverine.Fisher.IEventForwarding / EventForwardingTransform<T> are still covered by
// subscribe_to_event_requires_completion.
public class core_event_forwarding_5514
{
    // Store-agnostic configuration: nothing here names Fisher
    private static void forwardHappenedAsTranslated(IEventForwardingConfiguration forwarding)
    {
        forwarding.SubscribeToEvent<FiCoreForwardedHappened>()
            .TransformedTo(e => new FiCoreForwardedTranslated(e.Data.Name));
    }

    private static IHostBuilder hostFor(FisherTestDatabase database, Action<FisherIntegration> integration)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddFisher(m =>
                    {
                        m.Connection(database.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .IntegrateWithWolverine(integration);

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiCoreForwardedTranslatedHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
            });

    [Fact]
    public void the_integration_is_the_core_event_forwarding_and_hands_back_the_core_transform()
    {
        var integration = new FisherIntegration();
        integration.ShouldBeAssignableTo<IEventForwardingConfiguration>();

        integration.SubscribeToEvent<FiCoreForwardedHappened>()
            .ShouldBeAssignableTo<EventForwardingTransformation<FiCoreForwardedHappened>>();
    }

    [Fact]
    public async Task a_transformation_configured_through_the_core_interface_is_routed()
    {
        using var database = Servers.CreateDatabase("core_forwarding_routed");

        using var host = await hostFor(database, m => forwardHappenedAsTranslated(m))
            .StartAsync(TestContext.Current.CancellationToken);

        var routes = host.GetRuntime().RoutingFor(typeof(IEvent<FiCoreForwardedHappened>)).Routes;
        routes.ShouldContain(r => r.GetType().IsGenericType
                                  && r.GetType().GetGenericArguments().Last() == typeof(FiCoreForwardedTranslated));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_bare_subscription_through_the_core_interface_fails_at_bootstrap()
    {
        using var database = Servers.CreateDatabase("core_forwarding_bare");

        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            hostFor(database, m => ((IEventForwardingConfiguration)m).SubscribeToEvent<FiCoreForwardedHappened>())
                .StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(nameof(FiCoreForwardedHappened));
    }
}

public record FiCoreForwardedHappened(string Name);

public record FiCoreForwardedTranslated(string Name);

public static class FiCoreForwardedTranslatedHandler
{
    public static void Handle(FiCoreForwardedTranslated message)
    {
    }
}
