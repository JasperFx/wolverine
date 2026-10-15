using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

public record Gh5046RoutedLocally(Guid Id);

public record Gh5046NotHandledHere(Guid Id);

public static class Gh5046RoutedLocallyHandler
{
    public static void Handle(Gh5046RoutedLocally message)
    {
    }
}

/// <summary>
/// GH-5046. Under ServerlessWithOutbox the messages this application handles itself are routed to the durability pass
/// inbox at the durable local queue's address. Plain Serverless and the other modes route as they always did.
/// </summary>
public class serverless_with_outbox_local_routing_5046
{
    private static async Task<IHost> startAsync(DurabilityMode mode, Action<WolverineOptions>? configure = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5046RoutedLocallyHandler));
                configure?.Invoke(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_locally_handled_message_routes_to_the_pass_inbox()
    {
        using var host = await startAsync(DurabilityMode.ServerlessWithOutbox);
        var runtime = host.GetRuntime();

        var route = runtime.RoutingFor(typeof(Gh5046RoutedLocally)).Routes.Single().ShouldBeOfType<MessageRoute>();
        route.Uri.ShouldBe(new Uri("local://durable"));
        route.Sender.ShouldBeOfType<DurabilityPassInbox>();
        route.Sender.IsDurable.ShouldBeTrue();

        // Still no local queues in this mode
        runtime.Options.Transports.OfType<LocalTransport>().ShouldBeEmpty();
        runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri).ShouldBeSameAs(runtime.PassInbox);
    }

    [Fact]
    public async Task a_message_not_handled_here_gets_no_local_route()
    {
        using var host = await startAsync(DurabilityMode.ServerlessWithOutbox);

        host.GetRuntime().RoutingFor(typeof(Gh5046NotHandledHere)).Routes.ShouldBeEmpty();
    }

    [Fact]
    public async Task conventional_local_routing_turned_off_by_the_application_is_respected()
    {
        using var host = await startAsync(DurabilityMode.ServerlessWithOutbox,
            opts => opts.Policies.DisableConventionalLocalRouting());

        host.GetRuntime().RoutingFor(typeof(Gh5046RoutedLocally)).Routes.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_explicit_local_queue_rule_routes_to_the_pass_inbox_beside_the_other_explicit_routes()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await startAsync(DurabilityMode.ServerlessWithOutbox, opts =>
        {
            opts.Policies.DisableConventionalLocalRouting();
            opts.PublishMessage<Gh5046RoutedLocally>().ToLocalQueue("gh5046");
            opts.PublishMessage<Gh5046RoutedLocally>().ToPort(port);
        });

        var uris = host.GetRuntime().RoutingFor(typeof(Gh5046RoutedLocally)).Routes.OfType<MessageRoute>()
            .Select(x => x.Uri).ToArray();

        uris.Length.ShouldBe(2);
        uris.ShouldContain(new Uri("local://durable"));
        uris.ShouldContain(new Uri($"tcp://localhost:{port}"));
    }

    [Fact]
    public async Task plain_serverless_still_has_no_local_route()
    {
        using var host = await startAsync(DurabilityMode.Serverless);
        var runtime = host.GetRuntime();

        runtime.PassInbox.ShouldBeNull();
        runtime.RoutingFor(typeof(Gh5046RoutedLocally)).Routes.ShouldBeEmpty();
    }

    [Fact]
    public async Task solo_still_routes_to_a_local_queue()
    {
        using var host = await startAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        runtime.PassInbox.ShouldBeNull();
        var route = runtime.RoutingFor(typeof(Gh5046RoutedLocally)).Routes.Single().ShouldBeOfType<MessageRoute>();
        route.Sender.ShouldBeOfType<BufferedLocalQueue>();
    }
}
