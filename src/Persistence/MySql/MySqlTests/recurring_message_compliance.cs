using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;

namespace MySqlTests;

[Collection("mysql")]
public class recurring_message_compliance : RecurringMessageCompliance
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, Servers.DatabaseName("recurring_compliance"));
    }
}
