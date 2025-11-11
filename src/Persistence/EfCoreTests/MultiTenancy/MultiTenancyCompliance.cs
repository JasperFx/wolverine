using Alba;
using IntegrationTests;
using JasperFx.CommandLine;
using Marten.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SharedPersistenceModels;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Weasel.Postgresql;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

public enum DatabaseEngine
{
    SqlServer,
    PostgreSQL
}

[Collection("multi-tenancy")]
public abstract class MultiTenancyCompliance : IAsyncLifetime, IWolverineExtension
{
    protected IDbContextBuilder<ItemsDbContext> theBuilder;
    private readonly DatabaseEngine _engine;
    protected IAlbaHost theHost;
    protected string tenant1ConnectionString;
    protected string tenant2ConnectionString;
    protected string tenant3ConnectionString;
    
    protected MultiTenancyCompliance(DatabaseEngine engine)
    {
        JasperFxEnvironment.AutoStartHost = true;
        TestingOverrides.Extension = this;
        
        _engine = engine;
        if (engine == DatabaseEngine.SqlServer)
        {
            var builder = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString);
            builder.InitialCatalog = "db1";
            tenant1ConnectionString = builder.ConnectionString;

            builder.InitialCatalog = "db2";
            tenant2ConnectionString = builder.ConnectionString;

            builder.InitialCatalog = "db3";
            tenant3ConnectionString = builder.ConnectionString;
        }
        else
        {
            var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
            builder.Database = "db1";
            tenant1ConnectionString = builder.ConnectionString;

            builder.Database = "db2";
            tenant2ConnectionString = builder.ConnectionString;

            builder.Database = "db3";
            tenant3ConnectionString = builder.ConnectionString;
        }
        
    }

    public async Task InitializeAsync()
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            theHost = await AlbaHost.For<MultiTenantedEfCoreWithPostgreSQL.Program>(x => {});
        }
        else
        {
            theHost = await AlbaHost.For<MultiTenantedEfCoreWithSqlServer.Program>(x => {});
        }
        
        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<ItemsDbContext>>();
    }

    public Task DisposeAsync()
    {
        return theHost.StopAsync();
    }

    public abstract void Configure(WolverineOptions options);
    
    [Fact]
    public async Task db_context_is_wolverine_enabled()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var dbContext = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        dbContext.IsWolverineEnabled().ShouldBeTrue();
    }
    
    [Fact]
    public void can_resolve_db_context_options()
    {
        theHost.Services.GetRequiredService<DbContextOptions<ItemsDbContext>>()
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task can_build_a_db_context_at_all()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var dbContext = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        dbContext.ShouldNotBeNull();
    }
    
    [Fact]
    public async Task message_context_has_correct_envelope_transaction()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var dbContext = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);

        messageContext.Transaction.ShouldBeOfType<EfCoreEnvelopeTransaction>()
            .DbContext.ShouldBe(dbContext);
    }
    
    [Fact]
    public async Task end_to_end_with_commands()
    {
        var blueId = Guid.NewGuid();
        var redId = Guid.NewGuid();
        var greenId = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new StartNewItem(blueId, "Blue!"), "blue");
        await theHost.InvokeMessageAndWaitAsync(new StartNewItem(redId, "Red!"), "red");
        await theHost.InvokeMessageAndWaitAsync(new StartNewItem(greenId, "Green!"), "green");

        var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
        var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);

        (await blueDbContext.Items.FindAsync(blueId)).Name.ShouldBe("Blue!");
        (await greenDbContext.Items.FindAsync(blueId)).ShouldBeNull();
        (await redDbContext.Items.FindAsync(blueId)).ShouldBeNull();

        (await blueDbContext.Items.FindAsync(redId)).ShouldBeNull();
        (await greenDbContext.Items.FindAsync(redId)).ShouldBeNull();
        (await redDbContext.Items.FindAsync(redId)).Name.ShouldBe("Red!");

        (await blueDbContext.Items.FindAsync(greenId)).ShouldBeNull();
        (await greenDbContext.Items.FindAsync(greenId)).Name.ShouldBe("Green!");
        (await redDbContext.Items.FindAsync(greenId)).ShouldBeNull();
    }
    
    [Fact]
    public async Task end_to_end_with_default_database()
    {
        try
        {
            var defaultId = Guid.NewGuid();

            await theHost.InvokeMessageAndWaitAsync(new StartNewItem(defaultId, "The Default!"));
        
            var defaultDbContext = theBuilder.BuildForMain();
            var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
            var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
            var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);

            (await defaultDbContext.FindAsync<Item>(defaultId)).Name.ShouldBe("The Default!");
        }
        catch (DefaultTenantUsageDisabledException)
        {
            // For Marten, just let this go
        }
    }


    [Fact]
    public async Task with_http_posts_using_storage_actions()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());
        await theHost.Scenario(x =>
        {
            x.StatusCodeShouldBe(204);
            x.Post.Json(command).ToUrl("/item").QueryString("tenant", "blue");
            
        });
        
        var defaultDbContext = theBuilder.BuildForMain();
        var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
        var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);

        
        (await defaultDbContext.FindAsync<Item>(command.Id)).ShouldBeNull();
        (await redDbContext.FindAsync<Item>(command.Id)).ShouldBeNull();
        (await greenDbContext.FindAsync<Item>(command.Id)).ShouldBeNull();
        
        (await blueDbContext.FindAsync<Item>(command.Id)).Name.ShouldBe(command.Name);
    }

    [Fact]
    public async Task with_http_posts_using_storage_actions_using_default_tenant_id()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());
        await theHost.Scenario(x =>
        {
            // NO TENANT ID HERE!
            x.Post.Json(command).ToUrl("/item");
            x.StatusCodeShouldBe(204);
        });
        
        var defaultDbContext = theBuilder.BuildForMain();
        var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
        var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);
        
        (await blueDbContext.FindAsync<Item>(command.Id)).ShouldBeNull();
        (await redDbContext.FindAsync<Item>(command.Id)).ShouldBeNull();
        (await greenDbContext.FindAsync<Item>(command.Id)).ShouldBeNull();
        
        (await defaultDbContext.FindAsync<Item>(command.Id)).Name.ShouldBe(command.Name);
    }

    [Fact]
    public async Task http_get_with_direct_dependency()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());

        await theHost.InvokeMessageAndWaitAsync(command, "red");

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id).QueryString("tenant", "red");
        });
        
        result.ReadAsJson<Item>().Name.ShouldBe(command.Name);
        
        // Not found in other tenants
        await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id).QueryString("tenant", "blue");
            x.StatusCodeShouldBe(404);
        });
        
        await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id).QueryString("tenant", "green");
            x.StatusCodeShouldBe(404);
        });
        
        // Not in the default tenant
        await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id);
            x.StatusCodeShouldBe(404);
        });
    }
    
    [Fact]
    public async Task http_get_entity_attribute()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());

        await theHost.InvokeMessageAndWaitAsync(command, "red");

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/item2/" + command.Id).QueryString("tenant", "red");
        });
        
        result.ReadAsJson<Item>().Name.ShouldBe(command.Name);
        
        // Not found in other tenants
        await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id).QueryString("tenant", "blue");
            x.StatusCodeShouldBe(404);
        });
        
        await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id).QueryString("tenant", "green");
            x.StatusCodeShouldBe(404);
        });
        
        // Not in the default tenant
        await theHost.Scenario(x =>
        {
            x.Get.Url("/item1/" + command.Id);
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task http_post_with_direct_reference()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());

        await theHost.InvokeMessageAndWaitAsync(command, "green");
        await theHost.InvokeMessageAndWaitAsync(command, "blue");
        await theHost.InvokeMessageAndWaitAsync(command, "red");

        await theHost.Scenario(x =>
        {
            x.StatusCodeShouldBe(204);
            x
                .Post
                .Json(new ApproveItem1(command.Id))
                .QueryString("tenant", "green")
                .ToUrl("/item/approve1");
        });
        
        var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
        var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);
        
        (await blueDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeFalse();
        (await redDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeFalse();
        
        // Only approved this one
        (await greenDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeTrue();
    }
    
    [Fact]
    public async Task http_post_with_direct_reference_in_middleware()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());

        await theHost.InvokeMessageAndWaitAsync(command, "green");
        await theHost.InvokeMessageAndWaitAsync(command, "blue");
        await theHost.InvokeMessageAndWaitAsync(command, "red");

        await theHost.Scenario(x =>
        {
            x.StatusCodeShouldBe(204);
            x
                .Post
                .Json(new ApproveItem2(command.Id))
                .QueryString("tenant", "green")
                .ToUrl("/item/approve2");
        });
        
        var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
        var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);
        
        (await blueDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeFalse();
        (await redDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeFalse();
        
        // Only approved this one
        (await greenDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeTrue();
    }
    
    [Fact]
    public async Task http_post_with_by_entity_attribute_and_storage_action()
    {
        var command = new StartNewItem(Guid.NewGuid(), Guid.NewGuid().ToString());

        await theHost.InvokeMessageAndWaitAsync(command, "green");
        await theHost.InvokeMessageAndWaitAsync(command, "blue");
        await theHost.InvokeMessageAndWaitAsync(command, "red");

        await theHost.Scenario(x =>
        {
            x.StatusCodeShouldBe(204);
            x
                .Post
                .Json(new ApproveItem3(command.Id))
                .QueryString("tenant", "green")
                .ToUrl("/item/approve3");
        });
        
        var blueDbContext = await theBuilder.BuildAsync("blue", CancellationToken.None);
        var greenDbContext = await theBuilder.BuildAsync("green", CancellationToken.None);
        var redDbContext = await theBuilder.BuildAsync("red", CancellationToken.None);
        
        (await blueDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeFalse();
        (await redDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeFalse();
        
        // Only approved this one
        (await greenDbContext.FindAsync<Item>(command.Id)).Approved.ShouldBeTrue();
    }
    
    [Fact]
    public async Task use_sagas()
    {
        var command = new StartOrder(Guid.NewGuid().ToString());

        await theHost.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/orders/start").QueryString("tenant", "red");
            x.StatusCodeShouldBe(204);
        });

        await theHost.SendMessageAndWaitAsync(new CreditReserved(command.Id, "C1"),
            new DeliveryOptions { TenantId = "red" });

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/orders/" + command.Id).QueryString("tenant", "red");
        });
        
        result.ReadAsJson<Order>().OrderStatus.ShouldBe(OrderStatus.CreditReserved);

    }

    [Fact]
    public async Task use_db_context_outbox_end_to_end()
    {
        var factory = theHost.Services.GetRequiredService<IDbContextOutboxFactory>();
        var outbox = await factory.CreateForTenantAsync<ItemsDbContext>("blue", CancellationToken.None);

        outbox.TenantId.ShouldBe("blue");

        var id = Guid.NewGuid();
        
        await theHost.ExecuteAndWaitAsync(async _ =>
        {
            
            var item = new Item
            {
                Id = id,
                Name = "This one"
            };

            await outbox.PublishAsync(new ApproveItem1(id));
            outbox.DbContext.Items.Add(item);

            await outbox.SaveChangesAndFlushMessagesAsync();

        }, 120000);
        

        var builder = theHost.Services.GetRequiredService<IDbContextBuilder<ItemsDbContext>>();
        var dbContext = await builder.BuildAsync("blue", CancellationToken.None);

        var item2 = await dbContext.Items.FindAsync(id);
        item2.Approved.ShouldBeTrue();
    }

}