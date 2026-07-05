using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Xunit.Abstractions;
using RavenDbTests;

namespace RavenDbTests.LeaderElection;

/// <summary>
/// Runs the full leadership-election compliance battery while relying on the
/// native RavenDB control queue (ravendb://) for inter-node control
/// messaging, rather than <c>UseTcpForControlEndpoint()</c>. This exercises the
/// RavenDbControlTransport added in #3285 under real multi-node fan-out,
/// send-to-node, and leader-failover scenarios.
/// </summary>
[Collection("raven")]
public class control_queue_leadership_election_compliance : LeadershipElectionCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;

    public control_queue_leadership_election_compliance(ITestOutputHelper output, DatabaseFixture fixture) : base(output)
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
        // Deliberately NOT calling UseTcpForControlEndpoint() — the RavenDb
        // persistence registers its native control queue transport as the
        // NodeControlEndpoint when none is otherwise supplied.
        options.ServiceName = "raven";

        options.Services.AddSingleton(_store);
        options.UseRavenDbPersistence();
    }
}
