using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.Saga;

// GH-5572: retained, completed sagas as Marten documents
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "saga_retention";
            x.AutoCreateSchemaObjects = AutoCreate.All;
        }).IntegrateWithWolverine();
    }
}
