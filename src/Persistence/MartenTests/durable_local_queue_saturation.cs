using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;

namespace MartenTests;

public class durable_local_queue_saturation : DurableLocalQueueSaturationCompliance
{
    private const string SchemaName = "saturation";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = SchemaName;
                m.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddResourceSetupOnStartup();
    }

    protected override Task initialize()
    {
        return Host.ResetResourceState();
    }

    public override async Task<Todo?> Load(string id)
    {
        await using var session = Host.DocumentStore().QuerySession();
        return await session.LoadAsync<Todo>(id);
    }
}
