using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.Saga;

// GH-5839: completing a saga from outside its handlers, as Marten documents
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "saga_termination6";
            x.AutoCreateSchemaObjects = AutoCreate.All;
        }).IntegrateWithWolverine();
    }
}
