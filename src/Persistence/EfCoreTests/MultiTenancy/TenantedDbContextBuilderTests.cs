using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

public class TenantedDbContextBuilderTests : MultiTenancyContext
{
    private TenantedDbContextBuilder<ItemsDbContext> theBuilder;

    protected override Task onStartup()
    {
        var connectionStrings = new StaticConnectionStringSource();
        connectionStrings.Register("blue", tenant1ConnectionString);
        connectionStrings.Register("red", tenant2ConnectionString);
        connectionStrings.Register("green", tenant3ConnectionString);

        theBuilder = new TenantedDbContextBuilder<ItemsDbContext>(connectionStrings, (builder, connectionString) => builder.UseNpgsql(connectionString));

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
        builder.Database.ShouldBe("db1");
    }
    
    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_2()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "red";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db2");
    }
    
    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_3()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "greenm";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db3");
    }
    
    // TODO -- go end to end baby!
}