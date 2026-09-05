using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;

namespace FisherTests;

// GH-4310 (mirrored from Wolverine.Marten): SubscribeToEvent<T>() only opens a transformation — a
// bare call used to register nothing at all, leaving a "subscription" that silently never delivered
// anything. It is now a bootstrap-time configuration error unless event forwarding is enabled or the
// transformation is completed with TransformedTo().
public class subscribe_to_event_requires_completion
{
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

                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
            });

    [Fact]
    public async Task a_bare_subscribe_to_event_with_no_forwarding_fails_at_bootstrap_naming_the_fix()
    {
        using var database = Servers.CreateDatabase("subscribe_bare");

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => hostFor(database, m => m.SubscribeToEvent<SomethingHappened>()).StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(nameof(SomethingHappened));
        failure.Message.ShouldContain(nameof(FisherIntegration.UseFastEventForwarding));
        failure.Message.ShouldContain("TransformedTo");
    }

    [Fact]
    public async Task a_bare_subscribe_to_event_is_allowed_when_forwarding_is_on()
    {
        using var database = Servers.CreateDatabase("subscribe_forwarding");

        using var host = await hostFor(database, m =>
        {
            m.UseFastEventForwarding = true;
            m.SubscribeToEvent<SomethingHappened>();
        }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_completed_transformation_is_allowed_without_forwarding()
    {
        using var database = Servers.CreateDatabase("subscribe_transformed");

        // TransformedTo also serves events published by strictly-ordered subscriptions, so a
        // completed transformation without fast forwarding stays legal.
        using var host = await hostFor(database, m =>
        {
            m.SubscribeToEvent<SomethingHappened>()
                .TransformedTo(e => new SomethingTranslated(e.Data.Name));
        }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    public record SomethingHappened(string Name);

    public record SomethingTranslated(string Name);
}
