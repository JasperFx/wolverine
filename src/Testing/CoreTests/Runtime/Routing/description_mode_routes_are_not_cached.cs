using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Routing;

// GH-2897: routes built while WolverineSystemPart.WithinDescription is true are allowed to take a
// null MessageRoute.Sender (the endpoint's sending agent may not exist yet during
// FindResources/resource-setup-on-startup). If such a route is cached in the runtime's router
// cache, it later escapes onto the send path and NREs inside CreateForSending (the Envelope ctor
// dereferences agent.Endpoint). The fix: never cache description-mode routes.
public class description_mode_routes_are_not_cached
{
    [Fact]
    public async Task within_description_routing_is_not_cached_but_normal_routing_is()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug2897RoutingHandler>();
            }).StartAsync();

        var runtime = host.GetRuntime();

        // Baseline: normal-mode routing caches, so repeated lookups return the same router instance.
        runtime.ClearRoutingFor(typeof(Bug2897RoutedMessage));
        var normal1 = runtime.RoutingFor(typeof(Bug2897RoutedMessage));
        var normal2 = runtime.RoutingFor(typeof(Bug2897RoutedMessage));
        normal2.ShouldBeSameAs(normal1);

        // Description mode: routing must NOT be cached, so each lookup rebuilds a fresh router
        // and the (potentially null-Sender) description route can never poison the runtime cache.
        runtime.ClearRoutingFor(typeof(Bug2897RoutedMessage));
        WolverineSystemPart.WithinDescription = true;
        try
        {
            var d1 = runtime.RoutingFor(typeof(Bug2897RoutedMessage));
            var d2 = runtime.RoutingFor(typeof(Bug2897RoutedMessage));
            d2.ShouldNotBeSameAs(d1);
        }
        finally
        {
            WolverineSystemPart.WithinDescription = false;
        }

        // After description mode, the cache repopulates normally with live agents.
        runtime.ClearRoutingFor(typeof(Bug2897RoutedMessage));
        var after1 = runtime.RoutingFor(typeof(Bug2897RoutedMessage));
        runtime.RoutingFor(typeof(Bug2897RoutedMessage)).ShouldBeSameAs(after1);

        // And a real publish still works (the local route has a live, non-null Sender).
        await host.SendMessageAndWaitAsync(new Bug2897RoutedMessage("ok"));
    }
}

public record Bug2897RoutedMessage(string Text);

public class Bug2897RoutingHandler
{
    public void Handle(Bug2897RoutedMessage message) => _ = message;
}
