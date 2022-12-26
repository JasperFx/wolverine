using JasperFx.Core;
using Wolverine.Runtime;

namespace Wolverine.Transports.Stub;

internal class StubTransport : TransportBase<StubEndpoint>
{
    public StubTransport() : base("stub", "Stub")
    {
        Endpoints =
            new LightweightCache<string, StubEndpoint>(name => new StubEndpoint(name, this));
    }

    public new LightweightCache<string, StubEndpoint> Endpoints { get; }

    protected override IEnumerable<StubEndpoint> endpoints()
    {
        return Endpoints;
    }

    protected override StubEndpoint findEndpointByUri(Uri uri)
    {
        var name = uri.Host;
        return Endpoints[name];
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints) endpoint.Start(runtime.Pipeline, runtime.MessageLogger);

        return ValueTask.CompletedTask;
    }
}