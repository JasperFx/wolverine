using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4199, item 1. GH-4186 made <c>QueueCount</c> real for Inline and NativeAck, and in doing so turned a
/// dormant mis-render into a live one: EndpointCollection filled in <c>BufferLimit</c> for every listener
/// regardless of mode, but <see cref="Endpoint.ShouldEnforceBackPressure"/> is false for exactly those two
/// modes, so no BackPressureAgent is built and nothing ever reads that limit there. Before 6.31.0 the pair
/// read <c>(0, 1000)</c> and looked merely idle; afterwards it reads <c>234 of 1,000</c> and offers an
/// operator headroom that does not exist.
///
/// The fix is both halves: stop reporting the ceiling that is not enforced, and start reporting the one that
/// is -- the broker's prefetch window, via <see cref="Endpoint.InFlightLimit"/>.
/// </summary>
public class endpoint_health_ceilings_4199 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_native_ack_listener_reports_no_buffer_ceiling()
    {
        var endpoint = new NativeAckStubEndpoint("na-4199", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;

        // Deliberately set: the point is that a configured value is NOT reported, because nothing enforces it.
        endpoint.BufferingLimits = new BufferingLimits(1000, 500);

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);

        var snapshot = snapshotFor(endpoint.Uri);

        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
        snapshot.BufferLimit.ShouldBeNull(
            "A NativeAck listener builds no BackPressureAgent, so reporting a buffering ceiling offers headroom that does not exist.");
    }

    [Fact]
    public async Task an_inline_listener_reports_no_buffer_ceiling_either()
    {
        var endpoint = new StubEndpoint("inline-4199", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.Inline;
        endpoint.BufferingLimits = new BufferingLimits(1000, 500);

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);

        snapshotFor(endpoint.Uri).BufferLimit.ShouldBeNull();
    }

    /// <summary>
    /// The other half of the guard: the modes that DO enforce back pressure must keep reporting the ceiling,
    /// or this fix would have traded a mis-render for a blind spot.
    /// </summary>
    [Fact]
    public async Task a_buffered_listener_still_reports_its_buffer_ceiling()
    {
        var endpoint = new StubEndpoint("buffered-4199", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.BufferingLimits = new BufferingLimits(1000, 500);

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);

        endpoint.ShouldEnforceBackPressure().ShouldBeTrue();
        snapshotFor(endpoint.Uri).BufferLimit.ShouldBe(1000);
    }

    /// <summary>
    /// A transport with no prefetch window reports null rather than inventing a denominator. The stub is that
    /// transport; RabbitMQ and Azure Service Bus override <see cref="Endpoint.InFlightLimit"/> and are covered
    /// in their own suites, where a real broker applies the window.
    /// </summary>
    [Fact]
    public async Task no_in_flight_ceiling_is_reported_when_the_transport_has_none()
    {
        var endpoint = new NativeAckStubEndpoint("na-4199-noceiling", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);

        endpoint.InFlightLimit.ShouldBeNull();
        snapshotFor(endpoint.Uri).InFlightLimit.ShouldBeNull();
    }

    [Fact]
    public async Task the_transport_prefetch_window_reaches_the_snapshot()
    {
        var endpoint = new PrefetchingStubEndpoint("na-4199-prefetch", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;

        await theRuntime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);

        var snapshot = snapshotFor(endpoint.Uri);

        // The number an operator should read QueueCount against on this mode -- and the one that was
        // unreachable from outside Wolverine entirely before GH-4199.
        snapshot.InFlightLimit.ShouldBe(64);
        snapshot.BufferLimit.ShouldBeNull();
    }

    private EndpointHealthSnapshot snapshotFor(Uri uri)
    {
        return theRuntime.Endpoints.CollectEndpointHealth()
            .Single(x => x.Uri == uri && x.Direction == EndpointDirection.Listening);
    }
}

/// <summary>Stands in for a transport whose broker applies a prefetch window, as RabbitMQ's BasicQosAsync does.</summary>
internal class PrefetchingStubEndpoint : StubEndpoint
{
    public PrefetchingStubEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
    {
    }

    protected override bool supportsNativeAck => true;

    public override int? InFlightLimit => 64;
}
