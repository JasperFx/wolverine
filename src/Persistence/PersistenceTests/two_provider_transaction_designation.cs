using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests;

/// <summary>
///     GH-4631. A handler that depends on BOTH an EF Core <c>DbContext</c> and a Marten
///     <c>IDocumentSession</c> has two persistence providers that can own its transaction, and Wolverine
///     will not guess between them. Without a designation the chain fails to build; with one, the
///     designated provider commits.
/// </summary>
/// <remarks>
///     <para>
///         The bug this replaces was silence: <c>AutoApplyTransactions</c> applied transaction support only
///         when exactly ONE provider could apply, so a two-store handler got no middleware at all — no
///         <c>SaveChangesAsync</c> on either store, no log line, no failure. Both stores buffered their
///         writes and both discarded them at scope end.
///     </para>
///     <para>
///         <b>Two-provider atomicity is not supported, and this suite documents that rather than pretending
///         otherwise.</b> One chain gets one transaction from one provider; the other store's buffered work
///         is still discarded. The designation says which store's writes survive — it does not make the two
///         commit together. A handler that must write to two stores atomically needs a different design (one
///         store plus the outbox, or a saga).
///     </para>
/// </remarks>
public class two_provider_transaction_designation : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "if object_id('dbo.two_provider_widgets') is not null drop table dbo.two_provider_widgets;" +
            "create table dbo.two_provider_widgets (Id uniqueidentifier not null primary key, Name nvarchar(100) not null);";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task<IHost> startHostAsync(Type handlerType)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "two_provider_tx";
                    }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "two_provider_tx")
                    .UseLightweightSessions();

                opts.Services.AddDbContext<WidgetDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task an_ambiguous_two_provider_handler_fails_the_build()
    {
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await startHostAsync(typeof(AmbiguousWidgetHandler));
        });

        var text = ex.ToString();

        text.ShouldContain("EFCorePersistenceFrameProvider");
        text.ShouldContain("MartenPersistenceFrameProvider");
        text.ShouldContain("[Transactional(typeof(YourStorageType))]");
        text.ShouldContain("[NonTransactional]");
    }

    [Fact]
    public async Task the_designated_ef_core_dbcontext_commits()
    {
        using var host = await startHostAsync(typeof(EfCoreDesignatedWidgetHandler));

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new WriteToBothStoresForEfCore(id));

        (await widgetNameAsync(host, id)).ShouldBe("ef core");

        // Documenting, not asserting a feature: the un-designated store's buffered write is discarded.
        (await noteTextAsync(host, id)).ShouldBeNull();
    }

    [Fact]
    public async Task the_designated_marten_session_commits()
    {
        using var host = await startHostAsync(typeof(MartenDesignatedWidgetHandler));

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new WriteToBothStoresForMarten(id));

        (await noteTextAsync(host, id)).ShouldBe("marten");

        // Same documentation from the other side.
        (await widgetNameAsync(host, id)).ShouldBeNull();
    }

    private static async Task<string?> widgetNameAsync(IHost host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WidgetDbContext>();
        var widget = await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Widgets.AsNoTracking(), x => x.Id == id, TestContext.Current.CancellationToken);

        return widget?.Name;
    }

    private static async Task<string?> noteTextAsync(IHost host, Guid id)
    {
        await using var session = host.DocumentStore().QuerySession();
        var note = await session.LoadAsync<WidgetNote>(id, TestContext.Current.CancellationToken);

        return note?.Text;
    }
}

public class Widget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public class WidgetNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = null!;
}

public class WidgetDbContext : DbContext
{
    public WidgetDbContext(DbContextOptions<WidgetDbContext> options) : base(options)
    {
    }

    public DbSet<Widget> Widgets { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(map =>
        {
            map.ToTable("two_provider_widgets");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

public record WriteToBothStores(Guid Id);

public record WriteToBothStoresForEfCore(Guid Id);

public record WriteToBothStoresForMarten(Guid Id);

[WolverineIgnore]
public static class AmbiguousWidgetHandler
{
    public static void Handle(WriteToBothStores command, WidgetDbContext db, IDocumentSession session)
    {
        db.Widgets.Add(new Widget { Id = command.Id, Name = "ef core" });
        session.Store(new WidgetNote { Id = command.Id, Text = "marten" });
    }
}

[WolverineIgnore]
public static class EfCoreDesignatedWidgetHandler
{
    [Transactional(typeof(WidgetDbContext))]
    public static void Handle(WriteToBothStoresForEfCore command, WidgetDbContext db, IDocumentSession session)
    {
        db.Widgets.Add(new Widget { Id = command.Id, Name = "ef core" });
        session.Store(new WidgetNote { Id = command.Id, Text = "marten" });
    }
}

[WolverineIgnore]
public static class MartenDesignatedWidgetHandler
{
    [Transactional(typeof(IDocumentSession))]
    public static void Handle(WriteToBothStoresForMarten command, WidgetDbContext db, IDocumentSession session)
    {
        db.Widgets.Add(new Widget { Id = command.Id, Name = "ef core" });
        session.Store(new WidgetNote { Id = command.Id, Text = "marten" });
    }
}
