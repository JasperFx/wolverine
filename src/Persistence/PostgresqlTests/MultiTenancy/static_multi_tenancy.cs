using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

namespace PostgresqlTests.MultiTenancy;

public class static_multi_tenancy : MultiTenancyContext
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "static_multi_tenancy")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        opts.Services.AddResourceSetupOnStartup();
    }

    [Fact]
    public async Task registers_a_multi_tenanted_message_store()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();
        
        store.Master.Describe().DatabaseName.ShouldBe("postgres");
        
        (await store.Source.FindAsync("red")).Describe().DatabaseName.ShouldBe("db1");
        (await store.Source.FindAsync("blue")).Describe().DatabaseName.ShouldBe("db2");
        (await store.Source.FindAsync("green")).Describe().DatabaseName.ShouldBe("db3");
    }
}