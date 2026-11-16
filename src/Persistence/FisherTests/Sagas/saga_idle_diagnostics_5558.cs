using Fisher;
using JasperFx;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Fisher;

namespace FisherTests.Sagas;

// GH-5558: idle saga diagnostics over Fisher saga documents
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    private FisherTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase("saga_idle");
        opts.Durability.Mode = DurabilityMode.Solo;
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
