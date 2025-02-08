using IntegrationTests;
using JasperFx;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace EfCoreTests;

public class EFCorePersistenceContext : BaseContext
{
    public EFCorePersistenceContext() : base(true)
    {
        builder.ConfigureServices((c, services) =>
            {
                services.AddDbContext<SampleDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
                services.AddDbContext<SampleMappedDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
            })
            .UseWolverine(options =>
            {
                options.Services.AddSingleton<PassRecorder>();
                options.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                options.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                options.UseEntityFrameworkCoreTransactions();
                
                options.Policies.ConfigureConventionalLocalRouting()
                    .CustomizeQueues((_, q) => q.UseDurableInbox());
            });

        ItemsTable = new Table("items");
        ItemsTable.AddColumn<Guid>("Id").AsPrimaryKey();
        ItemsTable.AddColumn<string>("Name");
        ItemsTable.AddColumn<bool>("Approved");
    }

    public Table ItemsTable { get; }
}

[Collection("sqlserver")]
public class end_to_end_efcore_persistence : IClassFixture<EFCorePersistenceContext>
{
    public end_to_end_efcore_persistence(EFCorePersistenceContext context)
    {
        Host = context.theHost;
        ItemsTable = context.ItemsTable;
    }

    public Table ItemsTable { get; }

    public IHost Host { get; }
    
    
    [Fact]
    public async Task using_dbcontext_in_middleware()
    {
        await withItemsTable();
        
        var item = new Item { Id = Guid.NewGuid(), Name = "Hey"};
        await saveItem(item);

        await Host.InvokeMessageAndWaitAsync(new ApproveItem(item.Id));

        var existing = await loadItem(item.Id);
        existing.Approved.ShouldBeTrue();
    }

    private async Task<Item> loadItem(Guid id)
    {
        using var nested = Host.Services.CreateScope();

        var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
        var item = await context.Items.FindAsync(id);

        return item;
    }

    private async Task saveItem(Item item)
    {
        using var nested = Host.Services.CreateScope();

        var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
        context.Items.Add(item);
        await context.SaveChangesAsync();
    }

    [Fact]
    public void service_registrations()
    {
        var container = Host.Services.GetRequiredService<IServiceContainer>();

        container.DefaultFor<IDbContextOutbox>().Lifetime.ShouldBe(ServiceLifetime.Scoped);
        container.DefaultFor(typeof(IDbContextOutbox<>)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void outbox_for_specific_db_context_raw()
    {
        using var nested = Host.Services.CreateScope();

        var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
        var outbox = nested.ServiceProvider.GetRequiredService<IDbContextOutbox<SampleDbContext>>();

        outbox.DbContext.ShouldBeSameAs(context);
        outbox.ShouldBeOfType<DbContextOutbox<SampleDbContext>>()
            .Transaction.ShouldBeOfType<RawDatabaseEnvelopeTransaction>()
            .DbContext.ShouldBeSameAs(context);
    }

    [Fact]
    public void outbox_for_specific_db_context_maped()
    {
        using var nested = Host.Services.CreateScope();

        var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
        var outbox = nested.ServiceProvider.GetRequiredService<IDbContextOutbox<SampleMappedDbContext>>();

        outbox.DbContext.ShouldBeSameAs(context);
        outbox.ShouldBeOfType<DbContextOutbox<SampleMappedDbContext>>()
            .Transaction.ShouldBeOfType<MappedEnvelopeTransaction>()
            .DbContext.ShouldBeSameAs(context);
    }

    [Fact]
    public void outbox_for_db_context_raw()
    {
        using var nested = Host.Services.CreateScope();

        var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
        var outbox = nested.ServiceProvider.GetRequiredService<IDbContextOutbox>();

        outbox.Enroll(context);

        outbox.ActiveContext.ShouldBeSameAs(context);
        outbox.ShouldBeOfType<DbContextOutbox>()
            .Transaction.ShouldBeOfType<RawDatabaseEnvelopeTransaction>()
            .DbContext.ShouldBeSameAs(context);
    }

    [Fact]
    public void outbox_for_db_context_mapped()
    {
        var container = Host.Services.GetRequiredService<IServiceContainer>();
        using var nested = Host.Services.CreateScope();

        var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
        var outbox = nested.ServiceProvider.GetRequiredService<IDbContextOutbox>();

        outbox.Enroll(context);

        outbox.ActiveContext.ShouldBeSameAs(context);
        outbox.ShouldBeOfType<DbContextOutbox>()
            .Transaction.ShouldBeOfType<MappedEnvelopeTransaction>()
            .DbContext.ShouldBeSameAs(context);
    }

    [Fact]
    public async Task persist_an_outgoing_envelope_raw()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = [1, 2, 3, 4],
            OwnerId = 5,
            Destination = TransportConstants.RepliesUri,
            DeliverBy = new DateTimeOffset(DateTime.Today),
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        await withItemsTable();

        using (var nested = Host.Services.CreateScope())
        {
            var messaging = nested.ServiceProvider.GetRequiredService<IDbContextOutbox<SampleDbContext>>()
                .ShouldBeOfType<DbContextOutbox<SampleDbContext>>();

            await messaging.DbContext.Database.EnsureCreatedAsync();

            await messaging.Transaction.PersistOutgoingAsync(envelope);
            messaging.DbContext.Items.Add(new Item { Id = Guid.NewGuid(), Name = Guid.NewGuid().ToString() });

            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var persisted = await Host.Services.GetRequiredService<IMessageStore>()
            .Admin.AllOutgoingAsync();

        var loadedEnvelope = persisted.Single();

        loadedEnvelope.Id.ShouldBe(envelope.Id);

        loadedEnvelope.Destination.ShouldBe(envelope.Destination);
        loadedEnvelope.DeliverBy.ShouldBe(envelope.DeliverBy);
        loadedEnvelope.Data.ShouldBe(envelope.Data);


        loadedEnvelope.OwnerId.ShouldBe(envelope.OwnerId);
    }

    [Fact]
    public async Task persist_an_outgoing_envelope_mapped()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = [1, 2, 3, 4],
            OwnerId = 5,
            Destination = TransportConstants.RepliesUri,
            DeliverBy = new DateTimeOffset(DateTime.Today),
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        using (var nested = Host.Services.CreateScope())
        {
            var messaging = nested.ServiceProvider.GetRequiredService<IDbContextOutbox<SampleMappedDbContext>>()
                .ShouldBeOfType<DbContextOutbox<SampleMappedDbContext>>();

            await messaging.DbContext.Database.EnsureCreatedAsync();

            await messaging.Transaction.PersistOutgoingAsync(envelope);
            messaging.DbContext.Items.Add(new Item { Id = Guid.NewGuid(), Name = Guid.NewGuid().ToString() });

            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var persisted = await Host.Services.GetRequiredService<IMessageStore>()
            .Admin.AllOutgoingAsync();

        var loadedEnvelope = persisted.Single();

        loadedEnvelope.Id.ShouldBe(envelope.Id);

        loadedEnvelope.Destination.ShouldBe(envelope.Destination);
        loadedEnvelope.DeliverBy.ShouldBe(envelope.DeliverBy);
        loadedEnvelope.Data.ShouldBe(envelope.Data);


        loadedEnvelope.OwnerId.ShouldBe(envelope.OwnerId);
    }

    private async Task withItemsTable()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            var migration = await SchemaMigration.DetermineAsync(conn, ItemsTable);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                await new SqlServerMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
            }

            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task use_non_generic_outbox_raw()
    {
        var id = Guid.NewGuid();

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
            var messaging = nested.ServiceProvider.GetRequiredService<IDbContextOutbox>();

            messaging.Enroll(context);

            context.Items.Add(new Item { Id = id, Name = "Bill" });
            await messaging.SendAsync(new OutboxedMessage { Id = id });

            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
            (await context.Items.FindAsync(id)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task use_non_generic_outbox_mapped()
    {
        var id = Guid.NewGuid();

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
            var messaging = nested.ServiceProvider.GetRequiredService<IDbContextOutbox>();

            messaging.Enroll(context);

            context.Items.Add(new Item { Id = id, Name = "Bill" });
            await messaging.SendAsync(new OutboxedMessage { Id = id });

            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
            (await context.Items.FindAsync(id)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task use_generic_outbox_raw()
    {
        var id = Guid.NewGuid();

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();

        using (var nested = Host.Services.CreateScope())
        {
            var outbox = nested.ServiceProvider.GetRequiredService<IDbContextOutbox<SampleDbContext>>();

            outbox.DbContext.Items.Add(new Item { Id = id, Name = "Bill" });
            await outbox.SendAsync(new OutboxedMessage { Id = id });

            await outbox.SaveChangesAndFlushMessagesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
            (await context.Items.FindAsync(id)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task use_generic_outbox_mapped()
    {
        var id = Guid.NewGuid();

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();

        using (var nested = Host.Services.CreateScope())
        {
            var outbox = nested.ServiceProvider.GetRequiredService<IDbContextOutbox<SampleMappedDbContext>>();

            outbox.DbContext.Items.Add(new Item { Id = id, Name = "Bill" });
            await outbox.SendAsync(new OutboxedMessage { Id = id });

            await outbox.SaveChangesAndFlushMessagesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
            (await context.Items.FindAsync(id)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task persist_an_incoming_envelope_raw()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = [1, 2, 3, 4],
            OwnerId = 5,
            ScheduledTime = DateTime.Today.AddDays(1),
            DeliverBy = new DateTimeOffset(DateTime.Today),
            Status = EnvelopeStatus.Scheduled,
            Attempts = 2,
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType,
            Destination = TransportConstants.DurableLocalUri
        };

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleDbContext>();
            var messaging = nested.ServiceProvider.GetRequiredService<IDbContextOutbox>();

            messaging.Enroll(context);

            await messaging.As<MessageContext>().Transaction.PersistIncomingAsync(envelope);
            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var persisted = await Host.Services.GetRequiredService<IMessageStore>()
            .Admin.AllIncomingAsync();

        var loadedEnvelope = persisted.Single();

        loadedEnvelope.Id.ShouldBe(envelope.Id);

        loadedEnvelope.Destination.ShouldBe(envelope.Destination);
        loadedEnvelope.ScheduledTime.ShouldBe(envelope.ScheduledTime);
        loadedEnvelope.Data.ShouldBe(envelope.Data);
        loadedEnvelope.OwnerId.ShouldBe(envelope.OwnerId);
        loadedEnvelope.Attempts.ShouldBe(envelope.Attempts);
    }

    [Fact]
    public async Task persist_an_incoming_envelope_mapped()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = [1, 2, 3, 4],
            OwnerId = 5,
            ScheduledTime = DateTime.Today.AddDays(1),
            DeliverBy = new DateTimeOffset(DateTime.Today),
            Status = EnvelopeStatus.Scheduled,
            Attempts = 2,
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType,
            Destination = TransportConstants.DurableLocalUri
        };

        var container = Host.Services.GetRequiredService<IServiceContainer>();

        await withItemsTable();

        using (var nested = Host.Services.CreateScope())
        {
            var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
            var messaging = nested.ServiceProvider.GetRequiredService<IDbContextOutbox>();

            messaging.Enroll(context);

            await messaging.As<MessageContext>().Transaction.PersistIncomingAsync(envelope);
            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var persisted = await Host.Services.GetRequiredService<IMessageStore>()
            .Admin.AllIncomingAsync();

        var loadedEnvelope = persisted.Single();

        loadedEnvelope.Id.ShouldBe(envelope.Id);

        loadedEnvelope.Destination.ShouldBe(envelope.Destination);
        loadedEnvelope.ScheduledTime.ShouldBe(envelope.ScheduledTime);
        loadedEnvelope.Data.ShouldBe(envelope.Data);
        loadedEnvelope.OwnerId.ShouldBe(envelope.OwnerId);
        loadedEnvelope.Attempts.ShouldBe(envelope.Attempts);
    }

}

public class PassRecorder
{
    private readonly TaskCompletionSource<Pass> _completion = new();

    public Task<Pass> Actual => _completion.Task;

    public void Record(Pass pass)
    {
        _completion.SetResult(pass);
    }
}

public class PassHandler
{
    private readonly PassRecorder _recorder;

    public PassHandler(PassRecorder recorder)
    {
        _recorder = recorder;
    }

    public void Handle(Pass pass)
    {
        _recorder.Record(pass);
    }
}

public class Pass
{
    public string From { get; set; }
    public string To { get; set; }
}

public record ApproveItem(Guid Id);

public static class ApproveItemHandler
{
    public static ValueTask<Item> LoadAsync(ApproveItem command, SampleDbContext dbContext)
    {
        return dbContext.Items.FindAsync(command.Id);
    }

    [Transactional]
    public static void Handle(ApproveItem command, Item item)
    {
        item.Approved = true;
    }
}