using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace EfCoreTests;

public class durable_local_queue_saturation : DurableLocalQueueSaturationCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<TodoDbContext>(
            x => x.UseNpgsql(Servers.PostgresConnectionString), "saturation_ef");

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saturation_ef");

        opts.UseEntityFrameworkCoreTransactions();
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Policies.AutoApplyTransactions();

        opts.Services.AddResourceSetupOnStartup();
    }

    protected override Task initialize()
    {
        return Host.ResetResourceState();
    }

    public override async Task<Todo?> Load(string id)
    {
        using var scope = Host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        return await context.Todos.FindAsync(id);
    }
}
