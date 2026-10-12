using System.Text.Json.Serialization;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine.Attributes;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;
using Fisher;
using Marten;
using Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.ConcurrentAppends;

// GH-4940: a specification proves a Dynamic Consistency Boundary by landing a real concurrent append between the
// act's read and its write. Inside the boundary the act's save is refused with a DcbConcurrencyException, or a retry
// policy retries it and the retry succeeds; outside it, nothing is disturbed. On Marten, Polecat and Fisher.

// Guid-valued: Fisher serves a boundary aggregate over a Guid tag (a string one fails its stream id cast)
public record Sku(Guid Value);

public record ItemReserved(Sku Sku, int Quantity);

public record ReserveItem(Sku Sku, int Quantity);

public record ReserveItemWithRetry(Sku Sku, int Quantity);

[BoundaryAggregate]
public partial class StockLedger
{
    public int Reserved { get; private set; }

    public void Apply(ItemReserved e) => Reserved += e.Quantity;
}

public static class ReserveItemHandler
{
    public static EventTagQuery Load(ReserveItem command)
        => EventTagQuery.For(command.Sku).AndEventsOfType<ItemReserved>();

    public static ItemReserved Handle(ReserveItem command, [DcbModel] StockLedger? ledger)
        => (ledger?.Reserved ?? 0) + command.Quantity > 10
            ? throw new InvalidOperationException("Not enough stock")
            : new ItemReserved(command.Sku, command.Quantity);
}

public static class ReserveItemWithRetryHandler
{
    public static EventTagQuery Load(ReserveItemWithRetry command)
        => EventTagQuery.For(command.Sku).AndEventsOfType<ItemReserved>();

    // A conflict on the boundary is retried, and the retry reads the events that caused it
    [RetryNow(typeof(DcbConcurrencyException), 10, 10)]
    public static ItemReserved Handle(ReserveItemWithRetry command, [DcbModel] StockLedger? ledger)
        => (ledger?.Reserved ?? 0) + command.Quantity > 10
            ? throw new InvalidOperationException("Not enough stock")
            : new ItemReserved(command.Sku, command.Quantity);
}

// GH-4883: a DCB tag of exactly the shape --strong-typed-ids writes, and a stub-shaped command naming it Id
[JsonConverter(typeof(ShelfId.ShelfIdJsonConverter))]
public readonly record struct ShelfId(Guid Value) : IParsable<ShelfId>
{
    public static ShelfId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ShelfId result)
    {
        var parsed = Guid.TryParse(s, provider, out var value);
        result = new ShelfId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString();

    public sealed class ShelfIdJsonConverter : JsonConverter<ShelfId>
    {
        public override ShelfId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetGuid());

        public override void Write(Utf8JsonWriter writer, ShelfId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);

        public override ShelfId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(Guid.Parse(reader.GetString()!));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, ShelfId value, JsonSerializerOptions options)
            => writer.WritePropertyName(value.Value.ToString());
    }
}

public record StockShelf(ShelfId Id, int Quantity);

public record ShelfStocked(ShelfId Id, int Quantity);

[BoundaryAggregate]
public partial class ShelfLedger
{
    public int Stocked { get; private set; }

    public void Apply(ShelfStocked e) => Stocked += e.Quantity;
}

public static class StockShelfHandler
{
    public static EventTagQuery Load(StockShelf command)
        => EventTagQuery.For(command.Id).AndEventsOfType<ShelfStocked>();

    public static ShelfStocked Handle(StockShelf command, [DcbModel] ShelfLedger? ledger)
        => new(command.Id, command.Quantity);
}

public abstract class StockHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task BeforeStartAsync() => Task.CompletedTask;

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        await BeforeStartAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ReserveItemHandler))
                    .IncludeType(typeof(ReserveItemWithRetryHandler))
                    .IncludeType(typeof(StockShelfHandler));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenStockHost : StockHost
{
    protected override async Task BeforeStartAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var drop = conn.CreateCommand();
        drop.CommandText = "DROP SCHEMA IF EXISTS bobcat_concurrent_appends CASCADE;";
        await drop.ExecuteNonQueryAsync();
    }

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_concurrent_appends";
                m.DisableNpgsqlLogging = true;
                m.Events.RegisterTagType<Sku>("sku").ForAggregate<StockLedger>();
                m.Events.RegisterTagType<ShelfId>("shelf").ForAggregate<ShelfLedger>();
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => Host.Services.GetRequiredService<global::Marten.IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class PolecatStockHost : StockHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "bobcat_concurrent_dcb";
                m.Events.RegisterTagType<Sku>("sku").ForAggregate<StockLedger>();
                m.Events.RegisterTagType<ShelfId>("shelf").ForAggregate<ShelfLedger>();
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class FisherStockHost : StockHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_concurrent_appends_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
                o.Events.RegisterTagType<Sku>("sku").ForAggregate<StockLedger>();
                o.Events.RegisterTagType<ShelfId>("shelf").ForAggregate<ShelfLedger>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class concurrent_appends<THost>(THost app) : WolverineSpec(app.Host) where THost : StockHost
{
    private readonly Sku theSku = new(Guid.NewGuid());
    private readonly Sku anotherSku = new(Guid.NewGuid());

    [Fact]
    public async Task a_generated_strong_typed_id_is_a_dcb_tag_on_every_store()
    {
        // GH-4883: the shape the scaffold writes, as a tag: appended with the event, queried by the boundary
        var theShelf = new ShelfId(Guid.NewGuid());

        await WhenReceived(new StockShelf(theShelf, 2));
        ThenEvents(Tagged(new ShelfStocked(theShelf, 2), theShelf));

        await WhenReceived(new StockShelf(theShelf, 3));
        (await TheDcbModel<ShelfLedger>(EventTagQuery.For(theShelf).AndEventsOfType<ShelfStocked>()))!.Stocked.ShouldBe(5);
    }

    [Fact]
    public async Task a_concurrent_append_inside_the_boundary_refuses_the_act()
    {
        WhileActing(concurrently: Tagged(new ItemReserved(theSku, 1), theSku));

        await WhenReceived(new ReserveItem(theSku, 2));

        ThenRefusedWith<DcbConcurrencyException>();
        LastAct.ConcurrentAppendLanded.ShouldBe(true);
    }

    [Fact]
    public async Task a_concurrent_append_outside_the_boundary_does_not_disturb_the_act()
    {
        WhileActing(concurrently: Tagged(new ItemReserved(anotherSku, 1), anotherSku));

        await WhenReceived(new ReserveItem(theSku, 2));

        ThenEvents(new ItemReserved(theSku, 2));
        LastAct.ConcurrentAppendLanded.ShouldBe(true);
    }

    [Fact]
    public async Task a_retry_policy_retries_the_refused_act_and_the_retry_succeeds()
    {
        WhileActing(concurrently: Tagged(new ItemReserved(theSku, 1), theSku));

        await WhenReceived(new ReserveItemWithRetry(theSku, 2));

        ThenRetried();
        ThenEvents(new ItemReserved(theSku, 2));
        (await TheDcbModel<StockLedger>(EventTagQuery.For(theSku).AndEventsOfType<ItemReserved>()))!.Reserved.ShouldBe(3);
    }

    [Fact]
    public async Task without_a_conflict_the_act_is_not_retried()
    {
        await WhenReceived(new ReserveItemWithRetry(theSku, 2));

        Should.Throw<Exception>(() => ThenRetried()).Message.ShouldContain("first attempt");
    }

    [Fact]
    public async Task an_act_that_saves_nothing_never_lets_the_concurrent_writer_in_and_says_so()
    {
        WhileActing(concurrently: Tagged(new ItemReserved(theSku, 1), theSku));

        await WhenTracked("nothing is saved", () => Task.CompletedTask);

        LastAct.ConcurrentAppendLanded.ShouldBe(false);
        LastAct.Error!.Message.ShouldContain("saved nothing");
    }
}

public class concurrent_appends_on_marten(MartenStockHost app) : concurrent_appends<MartenStockHost>(app), IClassFixture<MartenStockHost>;

public class concurrent_appends_on_polecat(PolecatStockHost app) : concurrent_appends<PolecatStockHost>(app), IClassFixture<PolecatStockHost>;

public class concurrent_appends_on_fisher(FisherStockHost app) : concurrent_appends<FisherStockHost>(app), IClassFixture<FisherStockHost>;
