using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.RavenDb;
using Wolverine.Tracking;

namespace RavenDbTests;

// The RavenDb proof for [All] and [Queryable]. RavenDb has no event store integration in Wolverine, so
// there is no IEventStoreOperations coverage here. This suite only runs on CI.
[Collection("raven")]
public class all_and_queryable_attributes : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public all_and_queryable_attributes(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _store = _fixture.StartRavenStore();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RvWidgetHandler));
                opts.Services.AddSingleton<IDocumentStore>(_store);
                opts.UseRavenDbPersistence();
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task seedAndWait()
    {
        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new RvWidget { Name = "red", Hits = 5 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new RvWidget { Name = "green", Hits = 12 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new RvWidget { Name = "blue", Hits = 3 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // RavenDb indexes asynchronously, so wait for the writes to be queryable rather than racing them
        for (var i = 0; i < 20; i++)
        {
            using var session = _store.OpenAsyncSession();
            var count = await session.Query<RvWidget>()
                .Customize(x => x.WaitForNonStaleResults())
                .CountAsync(TestContext.Current.CancellationToken);
            if (count == 3) return;
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task all_gives_an_empty_list_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountRvWidgets());
        tracked.Sent.SingleMessage<RvWidgetsCounted>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task all_supplies_every_document()
    {
        await seedAndWait();
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountRvWidgets());
        tracked.Sent.SingleMessage<RvWidgetsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        await seedAndWait();
        var tracked = await _host.InvokeMessageAndWaitAsync(new FindPopularRvWidgets(4));
        tracked.Sent.SingleMessage<PopularRvWidgetsFound>().Names.ShouldBe(["green", "red"]);
    }
}

public class RvWidget
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public record CountRvWidgets;
public record FindPopularRvWidgets(int Minimum);
public record RvWidgetsCounted(int Count);
public record PopularRvWidgetsFound(string[] Names);

[WolverineIgnore]
public static class RvWidgetHandler
{
    public static RvWidgetsCounted Handle(CountRvWidgets command, [All] IReadOnlyList<RvWidget> widgets)
        => new(widgets.Count);

    // Async LINQ only, per the [Queryable] guidance
    public static async Task<PopularRvWidgetsFound> Handle(FindPopularRvWidgets command,
        [Queryable] IQueryable<RvWidget> widgets, CancellationToken token)
    {
        var names = await widgets.Where(x => x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Name)
            .ToListAsync(token);

        return new PopularRvWidgetsFound(names.ToArray());
    }

    public static void Handle(RvWidgetsCounted msg) { }
    public static void Handle(PopularRvWidgetsFound msg) { }
}
