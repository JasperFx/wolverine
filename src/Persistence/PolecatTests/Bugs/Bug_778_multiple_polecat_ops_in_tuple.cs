using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

public class Bug_778_multiple_polecat_ops_in_tuple
{
    [Fact]
    public async Task call_both_side_effects()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcSpawnHandler));

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "side_effects";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        var command = new PcSpawnTwo(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        await host.InvokeMessageAndWaitAsync(command);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var person1 = await session.LoadAsync<PcPerson>(command.Name1);
        var person2 = await session.LoadAsync<PcPerson>(command.Name2);

        person1.ShouldNotBeNull();
        person2.ShouldNotBeNull();
    }
}

public static class PcSpawnHandler
{
    public static (IPolecatOp, IPolecatOp) Handle(PcSpawnTwo command)
        => (PolecatOps.Store(new PcPerson(command.Name1)), PolecatOps.Store(new PcPerson(command.Name2)));
}

public record PcSpawnTwo(string Name1, string Name2);

public record PcPerson(string Id);
