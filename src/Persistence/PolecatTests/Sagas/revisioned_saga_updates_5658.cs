using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

// GH-5658: an IRevisioned saga as a Polecat document
public class revisioned_saga_updates_5658 : RevisionedSagaCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "revisioned_saga_updates";
        }).IntegrateWithWolverine();

        opts.Services.AddResourceSetupOnStartup();
    }
}
