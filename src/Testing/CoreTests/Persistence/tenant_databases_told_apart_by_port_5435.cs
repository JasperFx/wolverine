using CoreTests.Runtime;
using JasperFx.Core;
using JasperFx.Descriptors;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// GH-5435, found by the durability chaos rig. A relational store's agent Uri is
/// <c>wolverinedb://{engine}/{host}/{database}/{schema}</c>, without the port. So two tenant databases with one
/// name on one host but different ports, such as two PostgreSQL clusters or two containers on one box, shared
/// a Uri. Only the first got a durability agent: the second tenant's scheduled messages were never released,
/// and the work of its dead nodes was never recovered.
/// </summary>
public class tenant_databases_told_apart_by_port_5435
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly List<IMessageStore> theTenantDatabases = new();
    private readonly IMessageStore theMain;
    private readonly MessageStoreCollection theStores;

    public tenant_databases_told_apart_by_port_5435()
    {
        theRuntime.Options.Durability.TenantDatabaseListStaleTime = 0.Seconds();

        theSource.Cardinality.Returns(DatabaseCardinality.DynamicMultiple);
        theSource.AllActive().Returns(_ => theTenantDatabases.ToArray());

        theMain = aDatabase("main", 5432, MessageStoreRole.Main);

        theStores = new MessageStoreCollection(theRuntime,
            [new MultiTenantedMessageStore(theMain, theRuntime, theSource)], []);
    }

    /// <summary>
    /// A stand-in for a relational message store: its Uri leaves the port out until it is told to carry it
    /// </summary>
    private static IMessageStore aDatabase(string name, int port, MessageStoreRole role = MessageStoreRole.Tenant)
    {
        var store = Substitute.For<IMessageStore, IPortQualifiedAgentUri, IMessageStoreWithAgentSupport>();
        var uri = new Uri($"wolverinedb://postgresql/localhost/{name}/wolverine");

        store.Uri.Returns(_ => uri);
        store.Role.Returns(role);

        var qualifiable = (IPortQualifiedAgentUri)store;
        qualifiable.AgentUriPort.Returns(port);
        qualifiable.When(x => x.UsePortQualifiedUri())
            .Do(_ => uri = new Uri($"wolverinedb://postgresql/localhost:{port}/{name}/wolverine"));

        return store;
    }

    private async Task<string[]> theAgentUris() =>
        (await theStores.AllKnownAgentsAsync()).Select(x => x.ToString()).Order().ToArray();

    [Fact]
    public async Task databases_with_their_own_names_keep_the_uri_they_always_had()
    {
        theTenantDatabases.Add(aDatabase("red", 5433));
        theTenantDatabases.Add(aDatabase("blue", 5445));

        (await theStores.FindAllAsync()).Count.ShouldBe(3);

        theTenantDatabases.Select(x => x.Uri.ToString()).ShouldBe([
            "wolverinedb://postgresql/localhost/red/wolverine",
            "wolverinedb://postgresql/localhost/blue/wolverine"
        ]);
    }

    [Fact]
    public async Task two_databases_with_one_name_on_different_ports_each_get_an_agent()
    {
        var red = aDatabase("tenants", 5433);
        var blue = aDatabase("tenants", 5445);
        theTenantDatabases.Add(red);
        theTenantDatabases.Add(blue);

        (await theStores.FindAllAsync()).Count.ShouldBe(3);

        red.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost:5433/tenants/wolverine"));
        blue.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost:5445/tenants/wolverine"));

        (await theAgentUris()).ShouldBe([
            "wolverinedb://postgresql/localhost:5433/tenants/wolverine",
            "wolverinedb://postgresql/localhost:5445/tenants/wolverine",
            "wolverinedb://postgresql/localhost/main/wolverine"
        ]);

        (await theStores.FindDatabaseAsync(blue.Uri)).ShouldBeSameAs(blue);
        (await theStores.FindDatabaseAsync(red.Uri)).ShouldBeSameAs(red);
    }

    [Fact]
    public async Task a_colliding_database_found_later_is_told_apart_too()
    {
        theTenantDatabases.Add(aDatabase("tenants", 5433));
        theTenantDatabases.Add(aDatabase("tenants", 5445));
        await theStores.FindAllAsync();

        // A third one on yet another port, added at runtime after the first two were already told apart
        var green = aDatabase("tenants", 5446);
        theTenantDatabases.Add(green);
        (await theStores.FindAllAsync()).Count.ShouldBe(4);

        green.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost:5446/tenants/wolverine"));
        (await theAgentUris()).Length.ShouldBe(4);
    }

    [Fact]
    public async Task the_main_database_keeps_its_uri_when_a_tenant_database_collides_with_it()
    {
        var tenant = aDatabase("main", 5445);
        theTenantDatabases.Add(tenant);

        (await theStores.FindAllAsync()).Count.ShouldBe(2);

        theMain.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/main/wolverine"));
        tenant.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost:5445/main/wolverine"));
    }

    [Fact]
    public async Task the_same_database_reached_twice_is_not_a_collision()
    {
        var first = aDatabase("tenants", 5433);
        var second = aDatabase("tenants", 5433);
        theTenantDatabases.Add(first);
        theTenantDatabases.Add(second);

        await theStores.FindAllAsync();

        first.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/tenants/wolverine"));
        second.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/tenants/wolverine"));
    }
}
