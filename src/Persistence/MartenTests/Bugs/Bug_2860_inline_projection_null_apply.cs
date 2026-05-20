using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;

namespace MartenTests.Bugs;

// Regression guard for GH-2860. The issue was reported as an inline MultiStreamProjection with a
// null Apply silently aborting SaveChanges under a Wolverine-managed session, but the projection is
// a red herring: the real cause is that a handler which injects IDocumentSession, writes to it, and
// relies on Wolverine to persist will silently drop the writes UNLESS the chain has transactional
// middleware (AutoApplyTransactions(), [Transactional], an IMartenOp return, or a saga). Without it
// Wolverine opens the managed session to satisfy the parameter but never calls SaveChangesAsync, so
// the session is disposed with the writes discarded — no save, no exception. These guards pin the
// supported "Wolverine saves it" recipes. (Reproduces with or without the projection.)

public record Bug2860ItemRegistered(string Category);
public record Bug2860ItemRemoved(string Category);

public class Bug2860ItemStream
{
    public Guid Id { get; set; }
    public List<string> Categories { get; set; } = [];
    public void Apply(Bug2860ItemRegistered e) => Categories.Add(e.Category);
    public void Apply(Bug2860ItemRemoved e) => Categories.Remove(e.Category);
}

public record Bug2860RemoveItemCommand(Guid StreamId, string Category);

public static class Bug2860AutoApplyHandler
{
    public static async Task Handle(Bug2860RemoveItemCommand cmd, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<Bug2860ItemStream>(cmd.StreamId);
        stream.AppendMany(new Bug2860ItemRemoved(cmd.Category));
    }
}

public static class Bug2860TransactionalHandler
{
    [Transactional]
    public static async Task Handle(Bug2860RemoveItemCommand cmd, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<Bug2860ItemStream>(cmd.StreamId);
        stream.AppendMany(new Bug2860ItemRemoved(cmd.Category));
    }
}

public class Bug_2860_inline_projection_null_apply : PostgresqlContext
{
    private async Task<int> seedThenRemoveThroughWolverine(Type handlerType, bool autoApplyTransactions)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2860";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                if (autoApplyTransactions)
                {
                    opts.Policies.AutoApplyTransactions();
                }

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.CompletelyRemoveAllAsync();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var streamId = Guid.NewGuid();
        await using (var seed = store.LightweightSession())
        {
            seed.Events.StartStream<Bug2860ItemStream>(streamId, new Bug2860ItemRegistered("Electronics"));
            await seed.SaveChangesAsync();
        }

        await host.MessageBus().InvokeAsync(new Bug2860RemoveItemCommand(streamId, "Other"));

        await using var query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(streamId);
        return events.Count;
    }

    [Fact]
    public async Task auto_apply_transactions_persists_the_appended_event()
    {
        var count = await seedThenRemoveThroughWolverine(typeof(Bug2860AutoApplyHandler), autoApplyTransactions: true);
        count.ShouldBe(2);
    }

    [Fact]
    public async Task transactional_attribute_persists_the_appended_event()
    {
        var count = await seedThenRemoveThroughWolverine(typeof(Bug2860TransactionalHandler), autoApplyTransactions: false);
        count.ShouldBe(2);
    }
}
