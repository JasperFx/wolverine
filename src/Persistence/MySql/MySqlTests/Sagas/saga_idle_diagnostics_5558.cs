using Wolverine.RDBMS;
using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MySql;

namespace MySqlTests.Sagas;

// GH-5558: idle saga diagnostics over the lightweight MySQL saga tables
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<IdleDiagnosticsSaga>();
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "saga_idle_mysql");
    }
}
