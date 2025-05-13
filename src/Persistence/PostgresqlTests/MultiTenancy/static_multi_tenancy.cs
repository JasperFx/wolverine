using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using PostgresqlTests.Sagas;
using Shouldly;
using Weasel.Core.CommandLine;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Xunit.Abstractions;

namespace PostgresqlTests.MultiTenancy;

// TODO -- gotta repeat this test, but with NpgsqlDataSource instead. Ugh.
public class static_multi_tenancy : MultiTenancyContext
{
    private readonly ITestOutputHelper _output;

    public static_multi_tenancy(ITestOutputHelper output)
    {
        _output = output;
    }

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

        opts.AddSagaType<BlueSaga>("blues");
        opts.AddSagaType<RedSaga>("reds");
    }

    [Fact]
    public async Task registers_a_multi_tenanted_message_store()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();

        store.Main.Describe().DatabaseName.ShouldBe("postgres");

        (await store.Source.FindAsync("red")).Describe().DatabaseName.ShouldBe("db1");
        (await store.Source.FindAsync("blue")).Describe().DatabaseName.ShouldBe("db2");
        (await store.Source.FindAsync("green")).Describe().DatabaseName.ShouldBe("db3");
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
    public async Task all_databases_are_exposed_to_weasel()
    {
        var databases = await new WeaselInput().FilterDatabases(theHost);
        
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();
        
        databases.ShouldContain((IDatabase)store.Main);
        databases.ShouldContain((IDatabase)await store.Source.FindAsync("red"));
        databases.ShouldContain((IDatabase)await store.Source.FindAsync("blue"));
        databases.ShouldContain((IDatabase)await store.Source.FindAsync("green"));
    }

    [Fact]
    public async Task the_main_database_tables_include_node_persistence()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();
        var tables = await store.Main.As<PostgresqlMessageStore>().SchemaTables();

        var expected = @"
static_multi_tenancy.blues
static_multi_tenancy.reds
static_multi_tenancy.wolverine_control_queue
static_multi_tenancy.wolverine_dead_letters
static_multi_tenancy.wolverine_incoming_envelopes
static_multi_tenancy.wolverine_node_assignments
static_multi_tenancy.wolverine_node_records
static_multi_tenancy.wolverine_nodes
static_multi_tenancy.wolverine_outgoing_envelopes
".ReadLines().Where(x => x.IsNotEmpty()).ToArray();

        tables.OrderBy(x => x.QualifiedName).Select(x => x.QualifiedName).ToArray()
            .ShouldBe(expected);
    }

    [Fact]
    public async Task the_tenant_databases_have_only_envelope_and_saga_tables()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();

        await store.Source.RefreshAsync();

        var expected = @"
static_multi_tenancy.blues
static_multi_tenancy.reds
static_multi_tenancy.wolverine_dead_letters
static_multi_tenancy.wolverine_incoming_envelopes
static_multi_tenancy.wolverine_outgoing_envelopes
".ReadLines().Where(x => x.IsNotEmpty()).ToArray();

        foreach (var tenantId in new string[] { "red", "blue", "green" })
        {
            var messageStore = await store.Source.FindAsync(tenantId);
            var tables = await messageStore.As<PostgresqlMessageStore>().SchemaTables();

            tables.OrderBy(x => x.QualifiedName).Select(x => x.QualifiedName).ToArray()
                .ShouldBe(expected);
        }
    }


    /* TODO
     1. Test the right tables exist in both master and tenant databases
     2. check AllActive
     3. See all the right agents exist
     4. Add saga tables and see those tables exist
     5. Weasel databases, can find all



     */
}