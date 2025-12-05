using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.DomainEvents;

[Collection("sqlserver")]
public class configuration_of_domain_events_scrapers : IAsyncDisposable
{
    private IHost theHost;

    public configuration_of_domain_events_scrapers()
    {
        ItemsTable = new Table(new DbObjectName("dbo", "items"));
        ItemsTable.AddColumn<Guid>("Id").AsPrimaryKey();
        ItemsTable.AddColumn<string>("Name");
        ItemsTable.AddColumn<bool>("Approved");
    }

    public Table ItemsTable { get; }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    public async Task startHostAsync(Action<WolverineOptions> configure)
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotency");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
                
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddScoped<IEventPublisher, EventPublisher>();

                configure(opts);
                //opts.PublishDomainEventsFromEntityFrameworkCore();
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

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

    [Fact]
    public async Task publish_domain_events_with_DomainEvents()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore());

        using var scope = theHost.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<OutgoingDomainEvents>().ShouldNotBeNull();

        var container = theHost.Services.GetRequiredService<IServiceContainer>();
        container.DefaultFor<OutgoingDomainEvents>().Lifetime.ShouldBe(ServiceLifetime.Scoped);

        scope.ServiceProvider.GetServices<IDomainEventScraper>().Single().ShouldBeOfType<OutgoingDomainEventsScraper>();
    }
    
    [Fact]
    public async Task publish_domain_events_with_DomainEvents_using_dbcontextoutbox()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore());

        using var scope = theHost.Services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CleanDbContext>>();
        var tracked = await theHost.ExecuteAndWaitAsync(async _ =>
        {
            scope.ServiceProvider.GetRequiredService<OutgoingDomainEvents>().Add(new Event1("orange"));
            await context.SaveChangesAndFlushMessagesAsync();
        });

        tracked.MessageSucceeded.SingleMessage<Event1>().Color.ShouldBe("orange");
    }
    
        
    [Fact]
    public async Task publish_domain_events_with_DomainEvents_using_dbcontextoutbox_and_ieventpublisher()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore());

        using var scope = theHost.Services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<CleanDbContext>>();
        var tracked = await theHost.ExecuteAndWaitAsync(async _ =>
        {
            scope.ServiceProvider.GetRequiredService<IEventPublisher>().Publish(new Event1("orange"));
            await context.SaveChangesAndFlushMessagesAsync();
        });

        tracked.MessageSucceeded.SingleMessage<Event1>().Color.ShouldBe("orange");
    }

    [Fact]
    public async Task publish_through_outgoing_domain_events1()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore());
        
        var tracked =
            await theHost.InvokeMessageAndWaitAsync(new PublishEvents([
                new Event1("red"), new Event2("green"), new Event3("purple")
            ]));
        
        tracked.MessageSucceeded.SingleMessage<Event1>().Color.ShouldBe("red");
        tracked.MessageSucceeded.SingleMessage<Event2>().Color.ShouldBe("green");
        tracked.MessageSucceeded.SingleMessage<Event3>().Color.ShouldBe("purple");
    }
    

    [Fact]
    public async Task publish_through_outgoing_domain_events2()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore());
        
        var tracked =
            await theHost.InvokeMessageAndWaitAsync(new PublishEvents2([
                new Event1("red"), new Event2("green"), new Event3("purple")
            ]));
        
        tracked.MessageSucceeded.SingleMessage<Event1>().Color.ShouldBe("red");
        tracked.MessageSucceeded.SingleMessage<Event2>().Color.ShouldBe("green");
        tracked.MessageSucceeded.SingleMessage<Event3>().Color.ShouldBe("purple");
    }

    [Fact]
    public async Task publish_through_db_context_scraping1()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore<IEntity, IDomainEvent>(x => x.Events));

        var itemId = Guid.CreateVersion7();
        
        // Create an Item
        using (var scope = theHost.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
            
            var item = new Item { Id = itemId, Name = "Latte"};
            dbContext.Items.Add(item);
            await dbContext.SaveChangesAsync();
        }
        
        var tracked = await theHost.InvokeMessageAndWaitAsync(new ApproveItem(itemId));
        tracked.MessageSucceeded.SingleMessage<ItemApproved>().Id.ShouldBe(itemId);
    }
    
    [Fact]
    public async Task publish_through_db_context_scraping2()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore<Entity>(x => x.Events));

        var itemId = Guid.CreateVersion7();
        
        // Create an Item
        using (var scope = theHost.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
            
            var item = new Item { Id = itemId, Name = "Smoothie"};
            dbContext.Items.Add(item);
            await dbContext.SaveChangesAsync();
        }
        
        var tracked = await theHost.InvokeMessageAndWaitAsync(new ApproveItem(itemId));
        tracked.MessageSucceeded.SingleMessage<ItemApproved>().Id.ShouldBe(itemId);
    }
}

public record PublishEvents(object[] Events);
public record PublishEvents2(IDomainEvent[] Events);

public interface IEventPublisher
{
    void Publish<T>(T e) where T : IDomainEvent;
}

public class EventPublisher(OutgoingDomainEvents Events) : IEventPublisher
{
    public void Publish<T>(T e) where T : IDomainEvent
    {
        Events.Add(e);
    }
}

public record Event1(string Color) : IDomainEvent;

public record Event2(string Color) : IDomainEvent;

public record Event3(string Color) : IDomainEvent;

public static class PublishEventsHandler
{
    // Adding dbContext will make this apply EF Core middleware 
    public static void Handle(PublishEvents cmd, OutgoingDomainEvents events, CleanDbContext dbContext) 
    {
        events.AddRange(cmd.Events);
    }
    
    // Adding dbContext will make this apply EF Core middleware 
    public static void Handle(PublishEvents2 cmd, IEventPublisher publisher, CleanDbContext dbContext) 
    {
        foreach (var domainEvent in cmd.Events)
        {
            publisher.Publish(domainEvent);
        }
    }

    public static void Handle(Event1 e) => Debug.WriteLine($"Got Event1 of color {e.Color}");
    public static void Handle(Event2 e) => Debug.WriteLine($"Got Event1 of color {e.Color}");
    public static void Handle(Event3 e) => Debug.WriteLine($"Got Event1 of color {e.Color}");
}

public record ApproveItem(Guid Id);

public static class ApproveItemHandler
{
    public static IStorageAction<Item> Handle(ApproveItem command, [Entity] Item item)
    {
        // This publishes an event internally within the Item entity 
        // we want relayed to Wolverine
        item.Approve();
        return Storage.Update(item);
    }

    public static void Handle(ItemApproved e) => Debug.WriteLine($"Got item approved for {e.Id}");
}