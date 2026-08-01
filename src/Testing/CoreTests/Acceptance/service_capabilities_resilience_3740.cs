using JasperFx.Descriptors;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Acceptance;

// GH-3740: ServiceCapabilities.ReadFrom is a purely diagnostic snapshot of the application's own
// configuration, and it is read over and over by monitoring tools like CritterWatch. A single throwing
// property getter anywhere in the object graph used to abort the entire read, so a monitoring console
// received *no* capabilities at all -- forever, since every subsequent attempt re-ran the same doomed code.
// It's now best effort, at two levels:
//   * JasperFx 2.37.0 (jasperfx#590) guards the reflective getter walk itself, so a throwing property
//     costs you that one property -- recorded as OptionsValue.Unreadable -- and nothing more.
//   * Wolverine still guards each transport individually, for the failures OptionsDescription can't see
//     (a transport whose TryBuildBrokerUsage throws outright).
public class service_capabilities_resilience_3740
{
    [Fact]
    public async Task one_misbehaving_property_getter_costs_only_that_property()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Transports.Add(new BadlyBehavedTransport());
                opts.Transports.Add(new WellBehavedTransport());
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var capabilities =
            await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);

        // The broker whose getter throws is still described...
        var badlyBehaved =
            capabilities.Brokers.SingleOrDefault(x => x.ProtocolName == BadlyBehavedTransport.ProtocolName);
        badlyBehaved.ShouldNotBeNull();

        // ...with just the offending property standing in as unreadable
        var explosive = badlyBehaved.PropertyFor(nameof(BadlyBehavedTransport.Explosive));
        explosive.ShouldNotBeNull();
        explosive.Value.ShouldStartWith(OptionsValue.UnreadablePrefix);

        // ...and every other broker, and the rest of the snapshot, comes through
        capabilities.Brokers.ShouldContain(x => x.ProtocolName == WellBehavedTransport.ProtocolName);
        capabilities.PropertyFor("ServiceName").ShouldNotBeNull();
        capabilities.DurabilitySettings.ShouldNotBeNull();
    }

    [Fact]
    public async Task one_undescribable_transport_does_not_lose_the_whole_snapshot()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Transports.Add(new UndescribableTransport());
                opts.Transports.Add(new WellBehavedTransport());
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var capabilities =
            await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);

        // The broker that can't describe itself at all is simply missing...
        capabilities.Brokers.ShouldNotContain(x => x.ProtocolName == UndescribableTransport.ProtocolName);

        // ...while every other broker, and the rest of the snapshot, comes through
        capabilities.Brokers.ShouldContain(x => x.ProtocolName == WellBehavedTransport.ProtocolName);
        capabilities.PropertyFor("ServiceName").ShouldNotBeNull();
        capabilities.DurabilitySettings.ShouldNotBeNull();
    }

    [Fact]
    public async Task cancellation_is_not_swallowed_as_a_section_failure()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Transports.Add(new UndescribableTransport()))
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ServiceCapabilities.ReadFrom(host.GetRuntime(), null, cancellation.Token));
    }
}

internal class WellBehavedTransport : TransportBase<Endpoint>
{
    public const string ProtocolName = "well-behaved";

    public WellBehavedTransport() : base(ProtocolName, "Well Behaved Test Transport", [ProtocolName])
    {
    }

    protected override IEnumerable<Endpoint> endpoints() => [];

    protected override Endpoint findEndpointByUri(Uri uri) => throw new NotSupportedException();
}

internal class BadlyBehavedTransport : TransportBase<Endpoint>
{
    public const string ProtocolName = "badly-behaved";

    public BadlyBehavedTransport() : base(ProtocolName, "Badly Behaved Test Transport", [ProtocolName])
    {
    }

    /// <summary>
    /// Stands in for the real world case from GH-3740: AzureServiceBusTransport.HostName threw a
    /// NullReferenceException whenever the connection was credential-based rather than connection-string
    /// based, and JasperFx's OptionsDescription reads every public property reflectively.
    /// </summary>
    public string Explosive => throw new InvalidOperationException("Simulated bad property getter for GH-3740");

    protected override IEnumerable<Endpoint> endpoints() => [];

    protected override Endpoint findEndpointByUri(Uri uri) => throw new NotSupportedException();
}

/// <summary>
/// A transport that fails to describe itself at all, rather than merely owning one bad property getter.
/// OptionsDescription can't help here -- the throw happens before it ever runs -- so this is what exercises
/// Wolverine's own per-transport guard.
/// </summary>
internal class UndescribableTransport : TransportBase<Endpoint>
{
    public const string ProtocolName = "undescribable";

    public UndescribableTransport() : base(ProtocolName, "Undescribable Test Transport", [ProtocolName])
    {
    }

    public override bool TryBuildBrokerUsage(out BrokerDescription description) =>
        throw new InvalidOperationException("Simulated undescribable transport for GH-3740");

    protected override IEnumerable<Endpoint> endpoints() => [];

    protected override Endpoint findEndpointByUri(Uri uri) => throw new NotSupportedException();
}
