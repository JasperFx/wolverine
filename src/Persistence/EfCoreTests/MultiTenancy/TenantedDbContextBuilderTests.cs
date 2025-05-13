using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.MultiTenancy;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

public class TenantedDbContextBuilderTests : MultiTenancyContext
{
    private IDbContextBuilder<ItemsDbContext> theBuilder;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "static_multi_tenancy")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString) =>
            builder.UseNpgsql(connectionString));

        opts.Services.AddResourceSetupOnStartup();
    }

    protected override Task onStartup()
    {
        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<ItemsDbContext>>();
        return Task.CompletedTask;
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
    public async Task db_context_is_wolverine_enabled()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var dbContext = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        dbContext.IsWolverineEnabled().ShouldBeTrue();
    }

    [Fact]
    public async Task message_context_has_correct_envelope_transaction()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var dbContext = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        
        messageContext.Transaction.ShouldBeOfType<MappedEnvelopeTransaction>()
            .DbContext.ShouldBe(dbContext);
            
    }

    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_1()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db2");
    }
    
    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_2()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "red";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db1");
    }
    
    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_3()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "green";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db3");
    }
    
    // TODO -- go end to end baby!
}