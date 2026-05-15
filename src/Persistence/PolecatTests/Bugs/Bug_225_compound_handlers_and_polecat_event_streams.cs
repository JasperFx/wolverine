using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using JasperFx.Events;
using Polecat.Events;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

public class Bug_225_compound_handlers_and_polecat_event_streams
{
    [Fact]
    public async Task should_apply_transaction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_225";
                }).IntegrateWithWolverine();
            })
            .UseWolverine(opts => { opts.Policies.AutoApplyTransactions(); })
            .StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();

        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new PcStoreSomething2(id));

        await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var stream = await session.Events.FetchStreamAsync(id);

        stream.ShouldNotBeEmpty();
    }
}

public record PcStoreSomething2(Guid Id);

public class PcSomething
{
    public Guid Id { get; set; }

    public static PcSomething Create(PcStoreSomething2 ev)
    {
        return new PcSomething { Id = ev.Id };
    }
}

public class PcStoreSomething2CompoundHandler
{
    public static async Task<IEventStream<PcSomething>> LoadAsync(
        PcStoreSomething2 command,
        IDocumentSession session,
        CancellationToken ct)
    {
        return await session.Events.FetchForWriting<PcSomething>(command.Id, ct);
    }

    public static void Handle(PcStoreSomething2 command, IEventStream<PcSomething> stream)
    {
        stream.AppendOne(command);
    }
}
