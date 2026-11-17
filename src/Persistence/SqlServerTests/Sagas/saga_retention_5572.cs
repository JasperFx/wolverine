using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests.Sagas;

// GH-5572: retained, completed sagas in the lightweight SQL Server saga tables
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<RetainedSaga>();
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_retention_mssql");
    }
}
