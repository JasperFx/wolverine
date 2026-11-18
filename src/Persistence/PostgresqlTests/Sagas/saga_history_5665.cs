using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Postgresql;

namespace PostgresqlTests.Sagas;

// GH-5665: the message history of sagas in the lightweight PostgreSQL saga tables
public class saga_history_5665 : SagaHistoryCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_history_pg");
    }
}
