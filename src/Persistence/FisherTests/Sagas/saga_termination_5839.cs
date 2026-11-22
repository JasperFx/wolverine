using Fisher;
using JasperFx;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Fisher;

namespace FisherTests.Sagas;

// GH-5839: completing a saga from outside its handlers, as Fisher documents
public class saga_termination_5839 : SagaTerminationCompliance
{
    private FisherTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase("saga_termination");
        opts.Services.AddFisher(m =>
            {
                m.Connection(_database.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
    }

    protected override ValueTask CleanUpAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}
