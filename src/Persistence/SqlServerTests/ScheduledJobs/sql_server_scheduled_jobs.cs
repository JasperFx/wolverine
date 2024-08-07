using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.SqlServer;

namespace SqlServerTests.ScheduledJobs;

public class sql_server_scheduled_jobs : ScheduledJobCompliance
{
    public override void ConfigurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
    }
}