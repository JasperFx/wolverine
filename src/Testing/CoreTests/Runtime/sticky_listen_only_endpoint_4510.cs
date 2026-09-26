using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-4510, reported against 6.39.1. PrepopulateRoutingCache (new in 6.0) walks every discovered message type
/// during StartAsync. LocalTransport.DiscoverSenders yielded every endpoint a handler chain was sticky-bound
/// to as a local send candidate, so building the route called CreateSender on a receive-only endpoint --
/// an Azure Service Bus subscription in the report -- which threw NotSupportedException and took the whole
/// host down. On 5.x routes were built lazily on first send, so a message type that was only ever received
/// never hit this path at all.
///
/// <para>
/// A sticky binding means "deliver this message type to this listener". It was never a claim that anything
/// can send to the endpoint.
/// </para>
/// </summary>
public class sticky_listen_only_endpoint_4510
{
    [Fact]
    public async Task a_sticky_listen_only_endpoint_does_not_crash_startup()
    {
        // The reported configuration, minus the broker: a listen-only endpoint that is the exclusive
        // delivery channel for a handler, and a message type nothing ever publishes.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StickyOnlyHandler))
                    .IncludeType(typeof(SecondStickyOnlyHandler));

                // Sticky assignment only kicks in when a message type has more than one handler
                // (HandlerChain: `if (grouping.Count() > 1)`), which is exactly the reported pattern --
                // fanning one topic out to several exclusively-bound handlers.
                var endpoint = new ListenOnlyEndpoint();
                opts.Transports.GetOrCreate<StubbedListenOnlyTransport>().Registered.Add(endpoint);
                endpoint.StickyHandlers.Add(typeof(StickyOnlyHandler));

                var second = new ListenOnlyEndpoint(new Uri("listenonly://second"));
                opts.Transports.GetOrCreate<StubbedListenOnlyTransport>().Registered.Add(second);
                second.StickyHandlers.Add(typeof(SecondStickyOnlyHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        // Before the fix this threw NotSupportedException out of StartAsync itself.
        host.ShouldNotBeNull();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // ...and the listen-only endpoint is simply absent from the routes, rather than present-but-broken
        var routes = runtime.RoutingFor(typeof(StickyOnlyMessage));
        routes.Routes.OfType<MessageRoute>()
            .Any(x => x.Sender.Destination.Scheme == "listenonly")
            .ShouldBeFalse();
    }

    [Fact]
    public void an_ordinary_endpoint_still_reports_that_it_can_send()
    {
        // The guard is default-open, so this fix must not quietly drop every other sticky binding.
        new LocalQueue("ordinary").As<Endpoint>().CanSend.ShouldBeTrue();
    }

    [Fact]
    public void a_listen_only_endpoint_reports_that_it_cannot()
    {
        new ListenOnlyEndpoint().As<Endpoint>().CanSend.ShouldBeFalse();
    }
}

public record StickyOnlyMessage(string Name);

public static class StickyOnlyHandler
{
    public static void Handle(StickyOnlyMessage message)
    {
    }
}

public static class SecondStickyOnlyHandler
{
    public static void Handle(StickyOnlyMessage message)
    {
    }
}

/// <summary>
/// Stands in for an Azure Service Bus subscription or a Kafka topic group: receive-only, and its
/// CreateSender throws. Kept local to this test so the regression is covered in CoreTests without
/// needing either broker.
/// </summary>
public class ListenOnlyEndpoint : Endpoint
{
    public ListenOnlyEndpoint() : this(new Uri("listenonly://subscription"))
    {
    }

    public ListenOnlyEndpoint(Uri uri) : base(uri, EndpointRole.Application)
    {
        IsListener = true;
        EndpointName = uri.Host;
        Mode = EndpointMode.Inline;
    }

    protected internal override bool supportsSending => false;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new StubListener(Uri));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException(
            "This endpoint is listen-only, exactly like an Azure Service Bus subscription.");
    }
}

public class StubbedListenOnlyTransport : TransportBase<ListenOnlyEndpoint>
{
    public StubbedListenOnlyTransport() : base("listenonly", "Listen Only", [])
    {
    }

    public List<ListenOnlyEndpoint> Registered { get; } = new();

    protected override IEnumerable<ListenOnlyEndpoint> endpoints() => Registered;

    protected override ListenOnlyEndpoint findEndpointByUri(Uri uri) =>
        Registered.FirstOrDefault(x => x.Uri == uri) ?? Registered[0];
}

internal class StubListener(Uri address) : IListener
{
    public Uri Address { get; } = address;
    public IHandlerPipeline? Pipeline => null;
    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask StopAsync() => ValueTask.CompletedTask;
}
