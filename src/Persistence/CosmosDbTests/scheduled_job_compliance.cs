using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.CosmosDb;
using Wolverine.Util;

namespace CosmosDbTests;

[Collection("cosmosdb")]
public class scheduled_job_compliance : ScheduledJobCompliance
{
    private readonly AppFixture _fixture;

    public scheduled_job_compliance(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public override void ConfigurePersistence(WolverineOptions opts)
    {
        opts.Services.AddSingleton(_fixture.Client);
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);

        opts.Transports.NodeControlEndpoint =
            opts.Transports.GetOrCreateEndpoint(
                new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
    }
}
