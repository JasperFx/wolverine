using Fisher;
using JasperFx;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Fisher;

namespace FisherTests.Sagas;

// GH-5665: the message history of sagas kept as Fisher documents, written in the Fisher session that saves the saga
public class saga_history_5665 : SagaHistoryCompliance
{
    private FisherTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase("saga_history");
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
