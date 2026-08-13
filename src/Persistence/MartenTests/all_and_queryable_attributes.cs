using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests;

// [All] and [Queryable] are storage agnostic in the same way [FirstOrDefault] is -- the handlers below are
// what the Polecat, Fisher and EF Core suites run too.
public class all_and_queryable_attributes : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ColorHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "all_and_queryable";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();

        await _host.DocumentStore().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Color));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task seed() => _host.DocumentStore().BulkInsertDocumentsAsync(
    [
        new Color { Name = "red", Hits = 5 },
        new Color { Name = "green", Hits = 12 },
        new Color { Name = "blue", Hits = 3 }
    ], cancellation: TestContext.Current.CancellationToken);

    [Fact]
    public async Task all_gives_an_empty_list_rather_than_null_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountColors());

        tracked.Sent.SingleMessage<ColorsCounted>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task all_supplies_every_document()
    {
        await seed();

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountColors());

        tracked.Sent.SingleMessage<ColorsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        await seed();

        var tracked = await _host.InvokeMessageAndWaitAsync(new FindPopularColors(4));

        tracked.Sent.SingleMessage<PopularColorsFound>()
            .Names.ShouldBe(["green", "red"]);
    }
}

public class Color
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public record CountColors;

public record FindPopularColors(int Minimum);

public record ColorsCounted(int Count);

public record PopularColorsFound(string[] Names);

// [WolverineIgnore] so conventional discovery in other hosts in this assembly never picks these up
[WolverineIgnore]
public static class ColorHandler
{
    public static ColorsCounted Handle(CountColors command, [All] IReadOnlyList<Color> colors)
        => new(colors.Count);

    // The escape hatch: composing directly against the store's own LINQ provider.
    //
    // Note the Marten.ToListAsync() -- this handler is deliberately NOT portable, and that is the point of
    // the warnings on [Queryable]. Marten 9 refuses synchronous LINQ execution outright ("only asynchronous
    // data access is supported"), so the obvious .ToArray() that compiles fine and works on EF Core throws
    // at RUNTIME here.
    public static async Task<PopularColorsFound> Handle(FindPopularColors command,
        [Queryable] IQueryable<Color> colors, CancellationToken token)
    {
        var names = await colors.Where(x => x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Name)
            .ToListAsync(token);

        return new PopularColorsFound(names.ToArray());
    }

    public static void Handle(ColorsCounted msg) { }

    public static void Handle(PopularColorsFound msg) { }
}
