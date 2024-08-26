using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Embedded;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Xunit.Abstractions;

namespace RavenDbTests;

[Collection("raven")]
public class leadership_election_compliance : LeadershipElectionCompliance
{
    private IDocumentStore _store;

    public leadership_election_compliance(ITestOutputHelper output) : base(output)
    {
    }

    protected override async Task beforeBuildingHost()
    {
        _store = await EmbeddedServer.Instance.GetDocumentStoreAsync(Guid.NewGuid().ToString());
        
    }

    protected override void configureNode(WolverineOptions options)
    {
        var port = PortFinder.GetAvailablePort();
        var controlUri = $"tcp://localhost:{port}".ToUri();
        var controlPoint = options.Transports.GetOrCreateEndpoint(controlUri);

        options.ServiceName = "raven";
        options.Transports.NodeControlEndpoint = controlPoint;
        options.Services.AddSingleton(_store);
        options.UseRavenDbPersistence();
    }
}