using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Runtime;

namespace Wolverine.Transports.Stub;

internal class StubTransport : TransportBase<StubEndpoint>
{
    public StubTransport() : base("stub", "Stub")
    {
        Endpoints =
            new LightweightCache<Uri, StubEndpoint>(u => new StubEndpoint(u, this));
    }

    public new LightweightCache<Uri, StubEndpoint> Endpoints { get; }

    protected override IEnumerable<StubEndpoint> endpoints()
    {
        return Endpoints.GetAll();
    }

    protected override StubEndpoint findEndpointByUri(Uri uri)
    {
        return Endpoints[uri];
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints) endpoint.Start(runtime.Pipeline, runtime.MessageLogger);

        return ValueTask.CompletedTask;
    }
}
