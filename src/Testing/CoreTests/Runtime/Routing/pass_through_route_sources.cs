using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Routing;

/// <summary>
/// GH-4930. An additive route source found its routes, and then the first TERMINATING source after it -- the stub
/// transport, which always comes first among the built-in sources -- ended the chain on those routes even though
/// it had produced none itself. Explicit publishing rules and local routing were never consulted. A source that
/// implements <see cref="IPassThroughMessageRouteSource"/> with IsPassThrough = true keeps its routes out of that
/// check, so later sources route the message exactly as they would without it.
/// </summary>
public class pass_through_route_sources
{
    public record PassThroughMessage(Guid Id);
    public record HandledPassThroughMessage(Guid Id);

    public static class PassThroughHandler
    {
        public static void Handle(HandledPassThroughMessage message) { }
    }

    [Fact]
    public async Task without_pass_through_an_additive_source_still_ends_the_chain()
    {
        using var host = await startAsync(isPassThrough: false,
            opts => opts.PublishMessage<PassThroughMessage>().ToLocalQueue("explicit"));

        // The unchanged default: the explicit rule is never consulted
        routesFor<PassThroughMessage>(host).ShouldBe(["local://custom/"]);
    }

    [Fact]
    public async Task with_pass_through_later_explicit_rules_still_apply()
    {
        using var host = await startAsync(isPassThrough: true,
            opts => opts.PublishMessage<PassThroughMessage>().ToLocalQueue("explicit"));

        routesFor<PassThroughMessage>(host).OrderBy(x => x).ShouldBe(["local://custom/", "local://explicit/"]);

        var explanation = host.GetRuntime().ExplainRoutingFor(typeof(PassThroughMessage));
        explanation.Steps.Single(x => x.Source.Name == "ExplicitRouting").SkipReason.ShouldBeNull();

        // The explicit rule is terminating, so local routing after it is skipped as usual
        explanation.Steps.Single(x => x.Source.Name == "LocalRouting").SkipReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task with_pass_through_local_routing_to_a_handler_still_applies()
    {
        using var host = await startAsync(isPassThrough: true, _ => { });

        var routes = routesFor<HandledPassThroughMessage>(host);
        routes.ShouldContain("local://custom/");
        routes.ShouldContain(x => x != "local://custom/" && x.StartsWith("local://"));
    }

    [Fact]
    public async Task the_explanation_labels_a_pass_through_source()
    {
        using var host = await startAsync(isPassThrough: true, _ => { });

        var explanation = host.GetRuntime().ExplainRoutingFor(typeof(PassThroughMessage));

        var step = explanation.Steps.Single(x => x.Source.Name == nameof(CustomPassThroughSource));
        step.Source.IsPassThrough.ShouldBeTrue();
        explanation.ToText().ShouldContain($"SOURCE: {nameof(CustomPassThroughSource)} [additive, pass-through]");
    }

    private static Task<IHost> startAsync(bool isPassThrough, Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(PassThroughHandler));
                opts.PublishWithMessageRoutingSource(new CustomPassThroughSource { IsPassThrough = isPassThrough });
                configure(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static string[] routesFor<T>(IHost host)
    {
        return host.GetRuntime().RoutingFor(typeof(T)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ToArray();
    }

    /// <summary>
    /// Routes every message in this fixture to the local "custom" queue
    /// </summary>
    public class CustomPassThroughSource : IPassThroughMessageRouteSource
    {
        public bool IsPassThrough { get; init; }

        public bool IsAdditive => true;

        public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
        {
            if (messageType == typeof(PassThroughMessage) || messageType == typeof(HandledPassThroughMessage))
            {
                var queue = runtime.Endpoints.AgentForLocalQueue("custom");
                yield return MessageRoute.For(messageType, queue.Endpoint, runtime);
            }
        }
    }
}
