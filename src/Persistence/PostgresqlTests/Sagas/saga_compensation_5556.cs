using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Sagas;

// GH-5556: a compensating saga's status and log in the lightweight PostgreSQL saga tables
public class saga_compensation_5556 : SagaCompensationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<CompensatedSaga>();
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_compensation_pg");
    }
}
