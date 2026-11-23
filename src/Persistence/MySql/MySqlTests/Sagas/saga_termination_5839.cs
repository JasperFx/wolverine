using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MySql;
using Wolverine.RDBMS;

namespace MySqlTests.Sagas;

// GH-5839: completing a saga from outside its handlers, in the lightweight MySQL saga tables
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<TerminableSaga>();
        opts.AddSagaType<RetainedTerminableSaga>();
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "saga_termination_mysql");
    }
}
