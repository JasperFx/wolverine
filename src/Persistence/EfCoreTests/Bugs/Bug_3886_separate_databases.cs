using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// GH-3886, in the configuration the reporter actually runs: the two modules are SEPARATE PHYSICAL
// DATABASES, not two schemas in one database.
//
// This distinction is the whole point of the test, and it is worth keeping the more expensive setup
// for. With both modules in one database the GH-3886 misrouting looks like a cosmetic misplacement,
// because EfCoreEnvelopeTransaction.CommitAsync takes the SCHEMA from MessageContext.Storage (the
// wrongly-routed store) while taking the CONNECTION from the handler's own DbContext -- and in a
// single database that cross-wiring happens to succeed.
//
// Across real databases it cannot. Before the fix this test failed as:
//
//     Npgsql.PostgresException: 42P01: relation "..._wolverine.wolverine_incoming_envelopes" does not exist
//        at EfCoreEnvelopeTransaction.CommitAsync(...)
//     Envelope ... was moved to the error queue
//
// with module A's domain write rolled back and its message dead-lettered on the FIRST delivery,
// every time. A single-database test cannot catch that regression class.

public record MessageForTwoDatabases3886(Guid Id);

[StickyHandler(Bug_3886_separate_databases.QueueA)]
[Storage(typeof(DbA3886DbContext))]
public sealed class MessageForTwoDatabases3886AHandler
{
    public void Handle(MessageForTwoDatabases3886 message, DbA3886DbContext dbContext)
    {
        dbContext.SomeModels.Add(new DbA3886Model { Id = message.Id });
    }
}

[StickyHandler(Bug_3886_separate_databases.QueueB)]
[Storage(typeof(DbB3886DbContext))]
public sealed class MessageForTwoDatabases3886BHandler
{
    public void Handle(MessageForTwoDatabases3886 message, DbB3886DbContext dbContext)
    {
        dbContext.SomeModels.Add(new DbB3886Model { Id = message.Id });
    }
}

public sealed class DbA3886Model
{
    public Guid Id { get; set; }
}

public sealed class DbB3886Model
{
    public Guid Id { get; set; }
}

public sealed class DbA3886DbContext : DbContext
{
    public DbA3886DbContext(DbContextOptions<DbA3886DbContext> options) : base(options)
    {
    }

    public DbSet<DbA3886Model> SomeModels => Set<DbA3886Model>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("x3886_a");
        modelBuilder.Entity<DbA3886Model>().ToTable("some_models");
    }
}

public sealed class DbB3886DbContext : DbContext
{
    public DbB3886DbContext(DbContextOptions<DbB3886DbContext> options) : base(options)
    {
    }

    public DbSet<DbB3886Model> SomeModels => Set<DbB3886Model>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("x3886_b");
        modelBuilder.Entity<DbB3886Model>().ToTable("some_models");
    }
}

public class Bug_3886_separate_databases : IAsyncLifetime
{
    public const string QueueA = "bug3886x-a-queue";
    public const string QueueB = "bug3886x-b-queue";
    private const string Exchange = "bug3886x-exchange";

    // Derived from the lane's own database rather than a fixed literal, so parallelized CI lanes
    // sharing one server do not collide on the sibling database. See Servers.PostgresDatabaseName.
    private static readonly string DatabaseBName = $"{Servers.PostgresDatabaseName}_bug3886b";

    private static readonly string DatabaseB = Servers.PostgresConnectionString
        .Replace($"Database={Servers.PostgresDatabaseName}", $"Database={DatabaseBName}");

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await createModuleBDatabaseAsync();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MessageForTwoDatabases3886AHandler>()
                    .IncludeType<MessageForTwoDatabases3886BHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<MessageForTwoDatabases3886>()
                    .ToRabbitExchange(Exchange, e =>
                    {
                        e.BindQueue(QueueA);
                        e.BindQueue(QueueB);
                    })
                    .UseDurableOutbox();

                opts.ListenToRabbitQueue(QueueA).Named(QueueA).UseDurableInbox();
                opts.ListenToRabbitQueue(QueueB).Named(QueueB).UseDurableInbox();

                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<DbA3886DbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "x3886_a_wolverine");
                opts.Services.AddDbContextWithWolverineIntegration<DbB3886DbContext>(
                    x => x.UseNpgsql(DatabaseB), "x3886_b_wolverine");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "x3886_main");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "x3886_a_wolverine", MessageStoreRole.Ancillary).Enroll<DbA3886DbContext>();
                opts.PersistMessagesWithPostgresql(DatabaseB,
                    "x3886_b_wolverine", MessageStoreRole.Ancillary).Enroll<DbB3886DbContext>();

                opts.Services.AddResourceSetupOnStartup();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    private static async Task createModuleBDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var exists = conn.CreateCommand();
        exists.CommandText = "select 1 from pg_database where datname = @name";
        exists.Parameters.AddWithValue("name", DatabaseBName);

        if (await exists.ExecuteScalarAsync() != null) return;

        await using var create = conn.CreateCommand();
        // No parameters and no IF NOT EXISTS in PostgreSQL DDL; the name is derived from our own
        // connection string, not user input.
        create.CommandText = $"create database \"{DatabaseBName}\"";
        await create.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task each_module_commits_its_inbox_row_in_its_own_database()
    {
        var runtime = _host.GetRuntime();
        var messageTypeName = typeof(MessageForTwoDatabases3886).ToMessageTypeName();
        var id = Guid.NewGuid();

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new MessageForTwoDatabases3886(id));

        await Task.Delay(1000, TestContext.Current.CancellationToken);

        var storeA = runtime.Stores!.FindAncillaryStore(typeof(DbA3886DbContext));
        var storeB = runtime.Stores.FindAncillaryStore(typeof(DbB3886DbContext));

        var inA = (await storeA.Admin.AllIncomingAsync()).Where(x => x.MessageType == messageTypeName).ToArray();
        var inB = (await storeB.Admin.AllIncomingAsync()).Where(x => x.MessageType == messageTypeName).ToArray();

        inA.Length.ShouldBe(1, "The module A delivery belongs in module A's own database");
        inB.Length.ShouldBe(1, "The module B delivery belongs in module B's own database");

        inA.Single().Destination.ShouldBe(new Uri($"rabbitmq://queue/{QueueA}"));
        inB.Single().Destination.ShouldBe(new Uri($"rabbitmq://queue/{QueueB}"));

        // The sharpest signal: when the inbox row is routed to the wrong database the mark-as-handled
        // throws 42P01, the surrounding transaction rolls back, and the domain write is lost with the
        // message dead-lettered. A missing business row here means that has regressed.
        //
        // Asserted by id rather than by row count: ResetResourceState clears Wolverine's own stores
        // but not the application's model tables, so counts accumulate across runs.
        var token = TestContext.Current.CancellationToken;
        await using var scope = _host.Services.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<DbA3886DbContext>()
                .SomeModels.AnyAsync(x => x.Id == id, token))
            .ShouldBeTrue("Module A's domain write must survive -- a miss here means its transaction rolled back");
        (await scope.ServiceProvider.GetRequiredService<DbB3886DbContext>()
                .SomeModels.AnyAsync(x => x.Id == id, token))
            .ShouldBeTrue();
    }
}
