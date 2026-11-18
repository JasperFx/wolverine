using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MySql;
using Wolverine.RDBMS;

namespace MySqlTests.Sagas;

// GH-5556: a compensating saga's status and log in the lightweight MySQL saga tables
public class saga_compensation_5556 : SagaCompensationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<CompensatedSaga>();
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "saga_compensation_mysql");
    }
}
