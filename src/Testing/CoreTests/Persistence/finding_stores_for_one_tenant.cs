using CoreTests.Runtime;
using JasperFx.Descriptors;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// GH-4273. <see cref="MessageStoreCollection.FindForTenantAsync" /> became public API in #4268 without the
/// <c>_onlyOneDatabase</c> guard that every sibling lookup in the class opens with, so on a single-database
/// deployment it returned an EMPTY list for every tenant rather than the store holding their data.
///
/// <para>That is not an exotic configuration: <c>_onlyOneDatabase</c> is set whenever there is one store and
/// no <c>MultiTenantedMessageStore</c>, which is the shape of Marten <em>conjoined</em> tenancy -- one
/// database, a tenant_id column. Marten only builds a MultiTenantedMessageStore for master-table tenancy, so
/// a conjoined store falls through to a plain message store and <c>_multiTenanted</c> is empty.</para>
///
/// <para>The damage was silent. The method exists so a caller can query one tenant's dead letters without
/// hydrating every message body; an empty list reads as "this tenant has none" while its dead letter table
/// is full.</para>
/// </summary>
public class finding_stores_for_one_tenant
{
    private readonly MockWolverineRuntime theRuntime = new();

    private static IMessageStore aStore(string uri, MessageStoreRole role = MessageStoreRole.Main)
    {
        var store = Substitute.For<IMessageStore>();
        store.Uri.Returns(new Uri(uri));
        store.Role.Returns(role);
        return store;
    }

    [Fact]
    public async Task a_single_database_deployment_answers_with_the_main_store()
    {
        // One store, nothing multi-tenanted -- conjoined tenancy, or simply a single-database application.
        var main = aStore("wolverinedb://fake/main");
        var collection = new MessageStoreCollection(theRuntime, [main], []);

        var stores = await collection.FindForTenantAsync("acme");

        stores.Select(x => x.Uri).ShouldBe([main.Uri]);
    }

    [Fact]
    public async Task the_answer_does_not_depend_on_which_tenant_is_asked_for()
    {
        // Every tenant lives in the one database, so every tenant resolves to it -- including one this
        // process has never seen, which is the case the empty list was most misleading for.
        var main = aStore("wolverinedb://fake/main");
        var collection = new MessageStoreCollection(theRuntime, [main], []);

        (await collection.FindForTenantAsync("acme")).ShouldHaveSingleItem();
        (await collection.FindForTenantAsync("never-seen-before")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_tenanted_deployment_still_resolves_through_the_source()
    {
        // The guard must not short-circuit a genuinely multi-tenanted collection: this is the path the
        // method was written for, and it has to keep working.
        var source = Substitute.For<ITenantedMessageSource>();
        source.Cardinality.Returns(DatabaseCardinality.DynamicMultiple);
        source.AllActive().Returns(Array.Empty<IMessageStore>());

        var tenantStore = aStore("wolverinedb://fake/acme", MessageStoreRole.Ancillary);
        source.FindAsync("acme").Returns(tenantStore);

        var main = aStore("wolverinedb://fake/main");
        var multiTenanted = new MultiTenantedMessageStore(main, theRuntime, source);
        var collection = new MessageStoreCollection(theRuntime, [multiTenanted], []);

        var stores = await collection.FindForTenantAsync("acme");

        stores.Select(x => x.Uri).ShouldBe([tenantStore.Uri]);
        await source.Received(1).FindAsync("acme");
    }
}
