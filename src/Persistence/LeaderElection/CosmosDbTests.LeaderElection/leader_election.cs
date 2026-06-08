using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.ComplianceTests;
using Xunit.Abstractions;

namespace CosmosDbTests.LeaderElection;

[Collection("cosmosdb")]
public class leader_election(AppFixture fixture, ITestOutputHelper output)
    : LeadershipElectionCompliance(output)
{
    protected override void configureNode(WolverineOptions opts)
    {
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
        opts.Services.AddSingleton(fixture.Client);
    }

    protected override async Task beforeBuildingHost()
    {
        await fixture.InitializeAsync();
        await fixture.ClearAll();
    }
}
