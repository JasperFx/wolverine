using IntegrationTests;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.Marten;

namespace MartenTests.ScheduledJobs;

public class marten_scheduled_jobs : ScheduledJobCompliance
{
    public override void ConfigurePersistence(WolverineOptions opts)
    {
        opts.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();
    }
}

