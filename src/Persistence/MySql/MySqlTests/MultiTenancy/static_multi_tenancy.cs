using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Xunit.Abstractions;

namespace MySqlTests.MultiTenancy;

[Collection("mysql")]
public class static_multi_tenancy : MySqlMultiTenancyContext
{
    private readonly ITestOutputHelper _output;

    public static_multi_tenancy(ITestOutputHelper output)
    {
        _output = output;
    }

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "static_multi_tenancy")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        opts.Services.AddResourceSetupOnStartup();

        opts.AddSagaType<MySqlBlueSaga>("blues");
        opts.AddSagaType<MySqlRedSaga>("reds");
    }

    [Fact]
    public async Task registers_a_multi_tenanted_message_store()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();

        store.Main.Describe().DatabaseName.ShouldBe("wolverine");

        (await store.Source.FindAsync("red")).Describe().DatabaseName.ShouldBe("tenant_db1");
        (await store.Source.FindAsync("blue")).Describe().DatabaseName.ShouldBe("tenant_db2");
        (await store.Source.FindAsync("green")).Describe().DatabaseName.ShouldBe("tenant_db3");
    }

    [Fact]
    public async Task exposes_every_database_in_all_active()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();

        var all = store.ActiveDatabases();
        all.Count.ShouldBe(4);
    }

    [Fact]
    public async Task have_all_the_correct_durability_agents()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();

        var agents = await store.AllKnownAgentsAsync();
        agents.Count.ShouldBe(4); // Main + 3 tenants
    }
}

public class MySqlRedSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class MySqlBlueSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
