using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.RavenDb;
using Wolverine.Util;

namespace RavenDbTests;

[Collection("raven")]
public class scheduled_job_compliance : ScheduledJobCompliance
{
    private readonly DatabaseFixture _fixture;

    public scheduled_job_compliance(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public override void ConfigurePersistence(WolverineOptions opts)
    {
        opts.Services.AddSingleton(_fixture.StartRavenStore());
        opts.UseRavenDbPersistence();
        
        opts.Transports.NodeControlEndpoint =
            opts.Transports.GetOrCreateEndpoint(
                new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
    }
}