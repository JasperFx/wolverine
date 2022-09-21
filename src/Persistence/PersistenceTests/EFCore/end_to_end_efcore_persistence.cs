using System;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Baseline;
using IntegrationTests;
using Lamar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute.Extensions;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Transports;
using Xunit;

namespace PersistenceTests.EFCore;

public class EFCorePersistenceContext : BaseContext
{
    public EFCorePersistenceContext() : base(true)
    {
        builder.ConfigureServices((c, services) =>
            {
                services.AddDbContext<SampleDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
            })
            .UseWolverine(options =>
            {
                options.Services.AddSingleton<PassRecorder>();
                options.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                options.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                options.UseEntityFrameworkCorePersistence();

                options.Policies.UseConventionalLocalRouting()
                    .CustomizeQueues((_, q) => q.UseDurableInbox());
            });

        ItemsTable = new Table("items");
        ItemsTable.AddColumn<Guid>("Id").AsPrimaryKey();
        ItemsTable.AddColumn<string>("Name");

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
    public void service_registrations()
    {
        var container = (IContainer)Host.Services;
        
        container.Model.For<IDbContextOutbox>().Default.Lifetime.ShouldBe(ServiceLifetime.Scoped);
        container.Model.For(typeof(IDbContextOutbox<>)).Default.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void outbox_for_specific_db_context()
    {
        var container = (IContainer)Host.Services;
        using var nested = container.GetNestedContainer();

        var context = nested.GetInstance<SampleDbContext>();
        var outbox = nested.GetInstance<IDbContextOutbox<SampleDbContext>>();
        
        outbox.DbContext.ShouldBeSameAs(context);
        outbox.ShouldBeOfType<DbContextOutbox<SampleDbContext>>()
            .Transaction.ShouldBeOfType<EfCoreEnvelopeTransaction>()
            .DbContext.ShouldBeSameAs(context);
    }
    
    [Fact]
    public void outbox_for_db_context()
    {
        var container = (IContainer)Host.Services;
        using var nested = container.GetNestedContainer();

        var context = nested.GetInstance<SampleDbContext>();
        var outbox = nested.GetInstance<IDbContextOutbox>();
        
        outbox.Enroll(context);
        
        outbox.ActiveContext.ShouldBeSameAs(context);
        outbox.ShouldBeOfType<DbContextOutbox>()
            .Transaction.ShouldBeOfType<EfCoreEnvelopeTransaction>()
            .DbContext.ShouldBeSameAs(context);
    }
    
    [Fact]
    public async Task persist_an_outgoing_envelope()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = new byte[] { 1, 2, 3, 4 },
            OwnerId = 5,
            Destination = TransportConstants.RetryUri,
            DeliverBy = new DateTimeOffset(DateTime.Today),
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        var container = (IContainer)Host.Services;

        await withItemsTable();

        using (var nested = container.GetNestedContainer())
        {
            var messaging = nested.GetInstance<IDbContextOutbox<SampleDbContext>>()
                .ShouldBeOfType<DbContextOutbox<SampleDbContext>>();
            
            await messaging.DbContext.Database.EnsureCreatedAsync();
            
            await messaging.Transaction.PersistAsync(envelope);
            messaging.DbContext.Items.Add(new Item { Id = Guid.NewGuid(), Name = Guid.NewGuid().ToString() });

            await messaging.SaveChangesAndFlushMessagesAsync();
        }
        


        var persisted = await Host.Services.GetRequiredService<IEnvelopePersistence>()
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
        using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            var migration = await SchemaMigration.Determine(conn, ItemsTable);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                await new SqlServerMigrator().ApplyAll(conn, migration, AutoCreate.CreateOrUpdate);
            }
        }
    }

    [Fact]
    public async Task use_non_generic_outbox()
    {
        var id = Guid.NewGuid();
        
        var container = (IContainer)Host.Services;
        
        await withItemsTable();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();
        
        using (var nested = container.GetNestedContainer())
        {
            var context = nested.GetInstance<SampleDbContext>();
            var messaging = nested.GetInstance<IDbContextOutbox>();
        
            messaging.Enroll(context);

            context.Items.Add(new Item { Id = id, Name = "Bill" });
            await messaging.SendAsync(new OutboxedMessage { Id = id });
            
            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);
        
        using (var nested = container.GetNestedContainer())
        {
            var context = nested.GetInstance<SampleDbContext>();
            (await context.Items.FindAsync(id)).ShouldNotBeNull();
        }
        
        
    }

    [Fact]
    public async Task use_generic_outbox()
    {
        var id = Guid.NewGuid();
        
        var container = (IContainer)Host.Services;
        
        await withItemsTable();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();
        
        using (var nested = container.GetNestedContainer())
        {
            var outbox = nested.GetInstance<IDbContextOutbox<SampleDbContext>>();

            outbox.DbContext.Items.Add(new Item { Id = id, Name = "Bill" });
            await outbox.SendAsync(new OutboxedMessage { Id = id });
            
            await outbox.SaveChangesAndFlushMessagesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);
        
        using (var nested = container.GetNestedContainer())
        {
            var context = nested.GetInstance<SampleDbContext>();
            (await context.Items.FindAsync(id)).ShouldNotBeNull();
        }
        
        
    }

    
    [Fact]
    public async Task persist_an_incoming_envelope()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = new byte[] { 1, 2, 3, 4 },
            OwnerId = 5,
            ScheduledTime = DateTime.Today.AddDays(1),
            DeliverBy = new DateTimeOffset(DateTime.Today),
            Status = EnvelopeStatus.Scheduled,
            Attempts = 2,
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        var container = (IContainer)Host.Services;
        
        await withItemsTable();

        using (var nested = container.GetNestedContainer())
        {
            var context = nested.GetInstance<SampleDbContext>();
            var messaging = nested.GetInstance<IDbContextOutbox>();
        
            messaging.Enroll(context);

            await messaging.As<MessageContext>().Transaction.ScheduleJobAsync(envelope);
            await messaging.SaveChangesAndFlushMessagesAsync();
        }

        var persisted = await Host.Services.GetRequiredService<IEnvelopePersistence>()
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