using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_778_multiple_marten_ops_in_tuple : PostgresqlContext
{
    [Fact]
    public async Task call_both_side_effects()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SpawnHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "side_effects";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var command = new SpawnTwo(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        await host.InvokeMessageAndWaitAsync(command);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        var person1 = await session.LoadAsync<Person>(command.Name1);
        var person2 = await session.LoadAsync<Person>(command.Name2);

        person1.ShouldNotBeNull();
        person2.ShouldNotBeNull();


    }
}

public static class SpawnHandler
{
    public static (IMartenOp, IMartenOp) Handle(SpawnTwo command)
        => (MartenOps.Store(new Person(command.Name1)), MartenOps.Store(new Person(command.Name2)));
}

public record SpawnTwo(string Name1, string Name2);

public record Person(string Id);