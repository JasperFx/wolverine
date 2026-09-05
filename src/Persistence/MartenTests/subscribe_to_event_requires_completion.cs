using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

// GH-4310: SubscribeToEvent<T>() only opens a transformation — a bare call used to register
// nothing at all, leaving a "subscription" that silently never delivered anything. It is now a
// bootstrap-time configuration error unless event forwarding is enabled or the transformation is
// completed with TransformedTo().
public class subscribe_to_event_requires_completion : PostgresqlContext
{
    private static IHostBuilder hostFor(Action<MartenIntegration> integration)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(integration);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
            });

    [Fact]
    public async Task a_bare_subscribe_to_event_with_no_forwarding_fails_at_bootstrap_naming_the_fix()
    {
        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => hostFor(m => m.SubscribeToEvent<SomethingHappened>()).StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(nameof(SomethingHappened));
        failure.Message.ShouldContain(nameof(MartenIntegration.UseFastEventForwarding));
        failure.Message.ShouldContain("TransformedTo");
    }

    [Fact]
    public async Task a_bare_subscribe_to_event_is_allowed_when_forwarding_is_on()
    {
        using var host = await hostFor(m =>
        {
            m.UseFastEventForwarding = true;
            m.SubscribeToEvent<SomethingHappened>();
        }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_completed_transformation_is_allowed_without_forwarding()
    {
        // TransformedTo also serves events published by strictly-ordered subscriptions, so a
        // completed transformation without fast forwarding stays legal.
        using var host = await hostFor(m =>
        {
            m.SubscribeToEvent<SomethingHappened>()
                .TransformedTo(e => new SomethingTranslated(e.Data.Name));
        }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    public record SomethingHappened(string Name);

    public record SomethingTranslated(string Name);
}
