using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests.Sagas;

// GH-5556: a compensating saga's status and log in the lightweight SQL Server saga tables
public class saga_compensation_5556 : SagaCompensationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<CompensatedSaga>();
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_compensation_mssql");
    }
}
