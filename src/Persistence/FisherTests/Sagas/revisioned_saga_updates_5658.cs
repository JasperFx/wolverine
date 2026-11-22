using Fisher;
using JasperFx;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Fisher;

namespace FisherTests.Sagas;

// GH-5658: an IRevisioned saga as a Fisher document
public class revisioned_saga_updates_5658 : RevisionedSagaCompliance
{
    private FisherTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase("revisioned_saga_updates");
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
