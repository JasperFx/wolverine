using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Oracle;
using Wolverine.RDBMS;

namespace OracleTests.Sagas;

// GH-5572: retained, completed sagas in the lightweight Oracle saga tables
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<RetainedSaga>();
        opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
    }
}
