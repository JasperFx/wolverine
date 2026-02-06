using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Embedded;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit.Abstractions;
using RavenDbTests;

namespace RavenDbTests.LeaderElection;

[Collection("raven")]
public class leadership_election_compliance : LeadershipElectionCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store;

    public leadership_election_compliance(ITestOutputHelper output, DatabaseFixture fixture) : base(output)
    {
        _fixture = fixture;
    }

    protected override Task beforeBuildingHost()
    {
        _store = _fixture.StartRavenStore();
        return Task.CompletedTask;
    }

    protected override void configureNode(WolverineOptions options)
    {
        options.UseTcpForControlEndpoint();

        options.ServiceName = "raven";

        options.Services.AddSingleton(_store);
        options.UseRavenDbPersistence();
    }
}
