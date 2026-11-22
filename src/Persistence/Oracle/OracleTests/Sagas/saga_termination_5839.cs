using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Oracle;
using Wolverine.RDBMS;

namespace OracleTests.Sagas;

// GH-5839: completing a saga from outside its handlers, in the lightweight Oracle saga tables
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<TerminableSaga>();
        opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
    }
}
