using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Sagas;

// GH-5572: retained, completed sagas in the lightweight PostgreSQL saga tables
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<RetainedSaga>();
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_retention_pg");
    }
}
