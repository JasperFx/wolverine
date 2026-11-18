using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.Saga;

// GH-5556: a compensating saga's status and log as a Marten document
public class saga_compensation_5556 : SagaCompensationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "saga_compensation";
            x.AutoCreateSchemaObjects = AutoCreate.All;
        }).IntegrateWithWolverine();
    }
}
