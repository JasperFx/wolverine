using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class recurring_message_compliance : RecurringMessageCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "recurring_compliance");
    }
}
