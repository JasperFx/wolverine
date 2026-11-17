using Wolverine.RDBMS;
using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Postgresql;

namespace PostgresqlTests.Sagas;

// GH-5558: idle saga diagnostics over the lightweight PostgreSQL saga tables
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<IdleDiagnosticsSaga>();
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_idle_pg");
    }
}
