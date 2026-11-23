using IntegrationTests;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests.Sagas;

// GH-5839: completing a saga from outside its handlers, in the lightweight SQL Server saga tables
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<TerminableSaga>();
        opts.AddSagaType<RetainedTerminableSaga>();
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_termination_mssql");
    }
}
