using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.Saga;

// GH-5558: idle saga diagnostics over Marten saga documents (LastModified is mapped onto mt_last_modified)
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "saga_idle";
            x.AutoCreateSchemaObjects = AutoCreate.All;
        }).IntegrateWithWolverine();
    }
}
