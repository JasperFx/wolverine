using Fisher;
using JasperFx;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Fisher;

namespace FisherTests.Sagas;

// GH-5556: a compensating saga's status and log as a Fisher document
public class saga_compensation_5556 : SagaCompensationCompliance
{
    private FisherTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase("saga_compensation");
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
