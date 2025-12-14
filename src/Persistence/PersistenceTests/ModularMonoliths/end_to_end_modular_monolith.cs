using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;
using Marten;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Weasel.SqlServer;
using Wolverine.Configuration.Capabilities;
using Wolverine.Marten;
using Wolverine.Persistence;
using Xunit.Abstractions;

namespace PersistenceTests.ModularMonoliths;

public class MonolithFixture : IAsyncLifetime
{
    public MonolithFixture()
    {
        ItemsTable = new Table(new DbObjectName("mt_items", "items"));
        ItemsTable.AddColumn<Guid>("Id").AsPrimaryKey();
        ItemsTable.AddColumn<string>("Name");
        ItemsTable.AddColumn<bool>("Approved");
    }

    public IHost Host { get; private set; }

    public Table ItemsTable { get; }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var thingsConnectionString = await CreateDatabaseIfNotExists(conn, "things");
        await conn.CloseAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.AutoApplyTransactions();


                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StartAndTriggerApprovalHarness));

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<ApproveItem1>().ToRabbitQueue("items");
                opts.PublishMessage<ApproveThing>().ToRabbitQueue("things");

                // One EF Core integration...
                opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.UseEntityFrameworkCoreTransactions();

                // Ancillary Sql Server store
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, role: MessageStoreRole.Ancillary)
                    .Enroll<ItemsDbContext>();

                // Primary storage here...
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "marten";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                // Ancillary postgresql
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(thingsConnectionString);
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Make it empty...
        await Host.RebuildAllEnvelopeStorageAsync();

        await withItemsTable();
    }
    
    private async Task withItemsTable()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            var migration = await SchemaMigration.DetermineAsync(conn, ItemsTable);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                var sqlServerMigrator = new SqlServerMigrator();
                
                await sqlServerMigrator.ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
            }

            await conn.CloseAsync();
        }
    }

    public Task DisposeAsync()
    {
        return Host.StopAsync();
    }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }
}

public class end_to_end_modular_monolith : IClassFixture<MonolithFixture>, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly IHost theHost;

    public end_to_end_modular_monolith(MonolithFixture fixture, ITestOutputHelper output)
    {
        _output = output;
        theHost = fixture.Host;
    }

    public async Task InitializeAsync()
    {
        // Make it empty...
        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task system_capabilities_read_stores()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(theHost.GetRuntime(), null, CancellationToken.None);

        capabilities.MessageStores.Select(x => x.Uri)
             .ShouldBe([
                 new Uri("wolverinedb://postgresql/localhost/postgres/wolverine"),
                 new Uri("wolverinedb://postgresql/localhost/things/wolverine"),
                 new Uri("wolverinedb://sqlserver/localhost/master/wolverine")
             ]);
    }

    [Fact]
    public async Task capabilities_has_ancillary_store()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(theHost.GetRuntime(), null, CancellationToken.None);
        capabilities.EventStores.ShouldContain(x => x.SubjectUri == new Uri("marten://ithingstore"));
    }

    [Fact]
    public async Task capabilities_has_the_main_marten_store()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(theHost.GetRuntime(), null, CancellationToken.None);
        capabilities.EventStores.ShouldContain(x => x.SubjectUri == new Uri("marten://main"));
    }

    [Fact]
    public void has_ancillary_stores()
    {
        var runtime = theHost.GetRuntime();
        
        runtime.Stores.HasAnyAncillaryStores().ShouldBeTrue();
        runtime.Stores.HasAncillaryStoreFor(typeof(ItemsDbContext)).ShouldBeTrue();
        runtime.Stores.HasAncillaryStoreFor(typeof(IThingStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task has_all_the_expected_databases()
    {
        var runtime = theHost.GetRuntime();
        var databases = (await runtime.Stores.FindAllAsync()).Select(x => x.Uri).OrderBy(x => x.ToString()).ToArray();
        
        databases.ShouldBe([
            new Uri("wolverinedb://postgresql/localhost/postgres/wolverine"),
            new Uri("wolverinedb://postgresql/localhost/things/wolverine"),
            new Uri("wolverinedb://sqlserver/localhost/master/wolverine"),
            
            ]
        );
    }

    [Fact]
    public async Task using_db_context_outbox()
    {
        Func<IMessageContext, Task> action = async c =>
        {
            using var scope = theHost.Services.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<ItemsDbContext>>();
            var entity = new Item { Id = Guid.NewGuid() };
            outbox.DbContext.Items.Add(entity);
            await outbox.PublishAsync(new ApproveItem1(entity.Id));
            await outbox.SaveChangesAndFlushMessagesAsync();
        };
        
        var tracked = await theHost.TrackActivity()
            .ExecuteAndWaitAsync(action);

        tracked.Sent.SingleEnvelope<ApproveItem1>().ShouldNotBeNull();
    }

    [Fact]
    public async Task use_outbox_with_ancillary_store_with_ef_core()
    {
        var message = new StartAndTriggerApproval(Guid.NewGuid(), "Fix this.");
        var session = await theHost.SendMessageAndWaitAsync(message);

        var envelope = session.Sent.SingleEnvelope<ApproveItem1>();
        envelope.Store.Uri.ShouldBe(new Uri("wolverinedb://sqlserver/localhost/master/wolverine"));
        var messageId = envelope.Id;

        // Message should have been deleted
        var stored = await envelope.Store.Outbox.LoadOutgoingAsync(envelope.Destination);
        stored.Any(x => x.Id == messageId).ShouldBeFalse();
    }

    [Fact]
    public async Task use_inbox_with_ancillary_store_with_ef_core()
    {
        var message = new StartAndScheduleApproval(Guid.NewGuid(), "Rip in shirt");
        var session = await theHost.SendMessageAndWaitAsync(message);
        var scheduledEnvelope = session.Scheduled.SingleEnvelope<Envelope>();
        
        scheduledEnvelope.Store.Uri.ShouldBe(new Uri("wolverinedb://sqlserver/localhost/master/wolverine"));

        var stored = await scheduledEnvelope.Store.Admin.AllIncomingAsync();
        var persisted = stored.Where(x => x.MessageType == TransportConstants.ScheduledEnvelope).Single();
        persisted.Destination.ShouldBe(new Uri("local://durable"));
        
        var second = await session.PlayScheduledMessagesAsync(10.Seconds());
        var envelope = second.Sent.SingleEnvelope<ApproveItem1>();
        envelope.Destination.ShouldBe(new Uri("rabbitmq://queue/items"));

        var all = await theHost.GetRuntime().Stores.FindAllAsync();
        foreach (var store in all)
        {
            var outgoing = await store.Admin.AllOutgoingAsync();
            outgoing.Any().ShouldBeFalse($"Database {store.Uri} has {outgoing.Count} envelopes in the outbox");
        }

    }
    
    [Fact]
    public async Task use_outbox_with_ancillary_store_with_marten()
    {
        var message = new StartAndTriggerThing(Guid.NewGuid(), "Blue Shirt");
        var session = await theHost.SendMessageAndWaitAsync(message);

        var envelope = session.Sent.SingleEnvelope<ApproveThing>();
        envelope.Store.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/postgres/wolverine"));
        var messageId = envelope.Id;

        // Message should have been deleted
        var stored = await envelope.Store.Outbox.LoadOutgoingAsync(envelope.Destination);
        stored.Any(x => x.Id == messageId).ShouldBeFalse();
    }

    [Fact]
    public async Task use_inbox_with_ancillary_store_with_marten()
    {
        var message = new StartAndScheduleThing(Guid.NewGuid(), "Green Shirt");
        var session = await theHost.SendMessageAndWaitAsync(message);
        var scheduledEnvelope = session.Scheduled.SingleEnvelope<Envelope>();
        
        scheduledEnvelope.Store.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/postgres/wolverine"));

        var stored = await scheduledEnvelope.Store.Admin.AllIncomingAsync();
        var persisted = stored.Where(x => x.MessageType == TransportConstants.ScheduledEnvelope).Single();
        persisted.Destination.ShouldBe(new Uri("local://durable"));
        
        var second = await session.PlayScheduledMessagesAsync(10.Seconds());
        var envelope = second.Sent.SingleEnvelope<ApproveThing>();
        envelope.Destination.ShouldBe(new Uri("rabbitmq://queue/things"));

        var all = await theHost.GetRuntime().Stores.FindAllAsync();
        foreach (var store in all)
        {
            var outgoing = await store.Admin.AllOutgoingAsync();
            outgoing.Any().ShouldBeFalse($"Database {store.Uri} has {outgoing.Count} envelopes in the outbox");
        }

    }

}

public record ApproveItem1(Guid Id);

public record StartAndTriggerApproval(Guid Id, string Name);
public record StartAndScheduleApproval(Guid Id, string Name);


public static class StartAndTriggerApprovalHarness
{
    public static ApproveItem1 Handle(StartAndTriggerApproval command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item() { Id = command.Id, Name = command.Name });
        return new ApproveItem1(command.Id);
    }
    
    public static object Handle(StartAndScheduleApproval command, ItemsDbContext dbContext)
    {
        dbContext.Items.Add(new Item() { Id = command.Id, Name = command.Name });
        return new ApproveItem1(command.Id).DelayedFor(1.Hours());
    }
    
    public static (IStorageAction<Thing>, ApproveThing) Handle(StartAndTriggerThing command)
    {
        var storageAction = Storage.Insert(new Thing { Id = command.Id, Name = command.Name });
        return (storageAction, new ApproveThing(command.Id));
    }
    
    public static (IStorageAction<Thing>, object) Handle(StartAndScheduleThing command)
    {
        var storageAction = Storage.Insert(new Thing { Id = command.Id, Name = command.Name });
        return (storageAction, new ApproveThing(command.Id).DelayedFor(1.Hours()));
    }
}

public class ItemsDbContext : DbContext
{
    public ItemsDbContext(DbContextOptions<ItemsDbContext> options) : base(options)
    {
    }

    public DbSet<Item> Items { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your normal EF Core mapping
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("items", "mt_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id");
            map.Property(x => x.Name).HasColumnName("name");
            map.Property(x => x.Approved).HasColumnName("approved");
        });
    }
}

public class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    
    public bool Approved { get; set; }
}

public record StartAndTriggerThing(Guid Id, string Name);
public record StartAndScheduleThing(Guid Id, string Name);

public record ApproveThing(Guid Id);

public class Thing
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}



