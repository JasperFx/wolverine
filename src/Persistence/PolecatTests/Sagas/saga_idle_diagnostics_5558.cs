using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

// GH-5558: idle saga diagnostics over Polecat saga documents
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "saga_idle";
        }).IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();
    }
}
