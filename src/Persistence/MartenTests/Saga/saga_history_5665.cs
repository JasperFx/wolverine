using IntegrationTests;
using JasperFx;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Marten;

namespace MartenTests.Saga;

// GH-5665: the message history of sagas kept as Marten documents, written in the Marten session that saves the saga
public class saga_history_5665 : SagaHistoryCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "saga_history";
            x.AutoCreateSchemaObjects = AutoCreate.All;
        }).IntegrateWithWolverine(w => w.MessageStorageSchemaName = "saga_history_wolverine");
    }
}
