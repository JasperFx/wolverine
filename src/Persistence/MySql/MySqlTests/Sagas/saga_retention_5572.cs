using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MySql;
using Wolverine.RDBMS;

namespace MySqlTests.Sagas;

// GH-5572: retained, completed sagas in the lightweight MySQL saga tables
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<RetainedSaga>();
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "saga_retention_mysql");
    }
}
