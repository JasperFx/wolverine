using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

// GH-5665: the message history of sagas kept as Polecat documents, written in the Polecat session that saves the saga
public class saga_history_5665 : SagaHistoryCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "saga_history";
        }).IntegrateWithWolverine(w => w.MessageStorageSchemaName = "saga_history_wolverine");

        opts.Services.AddResourceSetupOnStartup();
    }
}
