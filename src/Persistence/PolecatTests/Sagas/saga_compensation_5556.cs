using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

// GH-5556: a compensating saga's status and log as a Polecat document
public class saga_compensation_5556 : SagaCompensationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "saga_compensation";
        }).IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();
    }
}
