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
                    .IncludeType(typeof(ReserveItemWithRetryHandler));
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
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class concurrent_appends<THost>(THost app) : WolverineSpec(app.Host) where THost : StockHost
{
    private readonly Sku theSku = new(Guid.NewGuid());
    private readonly Sku anotherSku = new(Guid.NewGuid());

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
