using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.SqlServer;

namespace SqlServerTests.Sagas;

// GH-5665: the message history of sagas in the lightweight SQL Server saga tables
public class saga_history_5665 : SagaHistoryCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_history_mssql");
    }
}
