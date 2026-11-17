using Wolverine.RDBMS;
using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.SqlServer;

namespace SqlServerTests.Sagas;

// GH-5558: idle saga diagnostics over the lightweight SQL Server saga tables
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<IdleDiagnosticsSaga>();
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_idle_mssql");
    }
}
