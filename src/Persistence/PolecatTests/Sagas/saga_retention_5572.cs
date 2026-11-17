using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

// GH-5572: retained, completed sagas as Polecat documents
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "saga_retention";
        }).IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();
    }
}
