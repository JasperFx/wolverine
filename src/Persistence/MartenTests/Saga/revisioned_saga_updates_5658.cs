using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.Saga;

// GH-5658: an IRevisioned saga as a Marten document
public class revisioned_saga_updates_5658 : RevisionedSagaCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "revisioned_saga_updates";
            x.AutoCreateSchemaObjects = AutoCreate.All;
        }).IntegrateWithWolverine();
    }
}
