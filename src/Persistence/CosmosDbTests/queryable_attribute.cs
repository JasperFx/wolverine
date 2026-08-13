using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.CosmosDb;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace CosmosDbTests;

/// <summary>
///     CosmosDb supports <c>[Queryable]</c> but deliberately NOT <c>[All]</c> or <c>[FirstOrDefault]</c>.
/// </summary>
/// <remarks>
///     Wolverine's CosmosDb integration writes every user document into one shared <c>wolverine</c> container
///     alongside its own envelopes and node records, with no per-type discriminator on user documents. So
///     "every document of type T" cannot be asked for safely, which is why <c>[All]</c> refuses the provider
///     outright. <c>[Queryable]</c> hands you the container's own queryable and leaves the filtering to you —
///     which is exactly why the query below filters on a discriminating property of its own rather than
///     trusting the container to hold only <c>CosmosWidget</c> documents.
///
///     This suite only runs on CI.
/// </remarks>
[Collection("cosmosdb")]
public class queryable_attribute : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _host = null!;

    public queryable_attribute(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CosmosWidgetHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        var container = _host.Services.GetRequiredService<Container>();
        foreach (var (name, hits) in new[] { ("red", 5), ("green", 12), ("blue", 3) })
        {
            await container.UpsertItemAsync(new CosmosWidget
            {
                id = Guid.NewGuid().ToString(), docType = "widget", Name = name, Hits = hits
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var tracked = await _host.InvokeMessageAndWaitAsync(new FindPopularCosmosWidgets(4));

        tracked.Sent.SingleMessage<PopularCosmosWidgetsFound>().Names.ShouldBe(["green", "red"]);
    }
}

public class CosmosWidget
{
    public string id { get; set; } = null!;

    // The shared container holds Wolverine's own documents too, so user documents that intend to be queried
    // as a set need a discriminator of their own. See the class remarks.
    public string docType { get; set; } = null!;

    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public record FindPopularCosmosWidgets(int Minimum);

public record PopularCosmosWidgetsFound(string[] Names);

[WolverineIgnore]
public static class CosmosWidgetHandler
{
    public static async Task<PopularCosmosWidgetsFound> Handle(FindPopularCosmosWidgets command,
        [Queryable] IQueryable<CosmosWidget> widgets, CancellationToken token)
    {
        // docType filter is NOT optional on Cosmos -- the container is shared
        using var iterator = widgets
            .Where(x => x.docType == "widget" && x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .ToFeedIterator();

        var names = new List<string>();
        while (iterator.HasMoreResults)
        {
            foreach (var widget in await iterator.ReadNextAsync(token))
            {
                names.Add(widget.Name);
            }
        }

        return new PopularCosmosWidgetsFound(names.ToArray());
    }

    public static void Handle(PopularCosmosWidgetsFound msg) { }
}
