using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

public class recurring_message_compliance : RecurringMessageCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "recurring_compliance");
    }
}
