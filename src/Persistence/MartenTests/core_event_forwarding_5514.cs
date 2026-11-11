using IntegrationTests;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace MartenTests;

// GH-5514: event forwarding configured once against the core IEventForwardingConfiguration, applied to Marten. The obsolete
// Wolverine.Marten.IEventForwarding / EventForwardingTransform<T> are still covered by event_streaming and
// subscribe_to_event_requires_completion.
public class core_event_forwarding_5514 : PostgresqlContext
{
    // Store-agnostic configuration: nothing here names Marten
    private static void forwardHappenedAsTranslated(IEventForwardingConfiguration forwarding)
    {
        forwarding.SubscribeToEvent<CoreForwardedHappened>()
            .TransformedTo(e => new CoreForwardedTranslated(e.Data.Name));
    }

    private static IHostBuilder hostFor(Action<MartenIntegration> integration)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "core_forwarding";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(integration);

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CoreForwardedTranslatedHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            });

    [Fact]
    public void the_integration_is_the_core_event_forwarding_and_hands_back_the_core_transform()
    {
        var integration = new MartenIntegration();
        integration.ShouldBeAssignableTo<IEventForwardingConfiguration>();

        // The existing public method keeps its signature until 8.0, but its value is already the core type
        integration.SubscribeToEvent<CoreForwardedHappened>()
            .ShouldBeAssignableTo<EventForwardingTransformation<CoreForwardedHappened>>();
    }

    [Fact]
    public async Task a_transformation_configured_through_the_core_interface_is_routed()
    {
        using var host = await hostFor(m => forwardHappenedAsTranslated(m)).StartAsync(TestContext.Current.CancellationToken);

        var routes = host.GetRuntime().RoutingFor(typeof(IEvent<CoreForwardedHappened>)).Routes;
        routes.ShouldContain(r => r.GetType().IsGenericType
                                  && r.GetType().GetGenericArguments().Last() == typeof(CoreForwardedTranslated));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_bare_subscription_through_the_core_interface_fails_at_bootstrap()
    {
        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            hostFor(m => ((IEventForwardingConfiguration)m).SubscribeToEvent<CoreForwardedHappened>())
                .StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(nameof(CoreForwardedHappened));
    }
}

public record CoreForwardedHappened(string Name);

public record CoreForwardedTranslated(string Name);

public static class CoreForwardedTranslatedHandler
{
    public static void Handle(CoreForwardedTranslated message)
    {
    }
}
