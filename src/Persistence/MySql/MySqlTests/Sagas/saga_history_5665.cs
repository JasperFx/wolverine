using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MySql;

namespace MySqlTests.Sagas;

// GH-5665: the message history of sagas in the lightweight MySQL saga tables
public class saga_history_5665 : SagaHistoryCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "saga_history_mysql");
    }
}
