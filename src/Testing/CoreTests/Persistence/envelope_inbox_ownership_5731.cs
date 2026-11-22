using CoreTests.Runtime;
using JasperFx;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

// GH-5731 / GH-5732. Can a handler's transaction against a store mark an envelope's inbox row handled? Only when
// that store's database holds the row. A wrong "yes" marks zero rows in the wrong database and flips the status that
// tells DurableReceiver to mark the row itself, so the row stays Incoming and is replayed on every restart. A wrong
// "no" only costs the mark riding the handler's transaction.
public class envelope_inbox_ownership_5731
{
    private static IMessageStore store(string uri, MessageStoreRole role)
    {
        var store = Substitute.For<IMessageStore>();
        store.Uri.Returns(new Uri(uri));
        store.Role.Returns(role);
        return store;
    }

    private static MultiTenantedMessageStore tenanted(IMessageStore main, params IMessageStore[] tenants)
    {
        var runtime = new MockWolverineRuntime();
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        var source = Substitute.For<ITenantedMessageSource>();
        source.AllActive().Returns(tenants);

        return new MultiTenantedMessageStore(main, runtime, source);
    }

    private readonly IMessageStore theMain = store("wolverinedb://sqlserver/server/main/wolverine", MessageStoreRole.Main);
    private readonly IMessageStore storeA = store("wolverinedb://sqlserver/server/a/wolverine", MessageStoreRole.Ancillary);
    private readonly IMessageStore storeB = store("wolverinedb://sqlserver/server/b/wolverine", MessageStoreRole.Ancillary);

    [Fact]
    public void no_store_on_the_envelope_means_the_main_store()
    {
        var envelope = new Envelope();

        envelope.InboxRowIsIn(theMain, theMain).ShouldBeTrue();
        envelope.InboxRowIsIn(storeB, theMain).ShouldBeFalse();
    }

    [Fact]
    public void row_written_in_one_ancillary_store_is_not_in_another()
    {
        var envelope = new Envelope { Store = storeA };

        envelope.InboxRowIsIn(storeA, theMain).ShouldBeTrue();
        envelope.InboxRowIsIn(storeB, theMain).ShouldBeFalse();
        envelope.InboxRowIsIn(theMain, theMain).ShouldBeFalse();
    }

    [Fact]
    public void same_database_and_schema_is_the_same_store()
    {
        var sameAsA = store("wolverinedb://sqlserver/server/a/wolverine", MessageStoreRole.Ancillary);
        new Envelope { Store = storeA }.InboxRowIsIn(sameAsA, theMain).ShouldBeTrue();
    }

    [Fact]
    public void tenant_database_of_another_ancillary_store_is_not_the_main_stores()
    {
        // GH-5732 with RegisterTenants: the tenant database of ancillary store B has role Tenant, not Ancillary
        var b1 = store("wolverinedb://sqlserver/server/b1/wolverine", MessageStoreRole.Tenant);

        new Envelope().InboxRowIsIn(b1, theMain).ShouldBeFalse();
    }

    [Fact]
    public void tenant_database_of_the_main_store_holds_its_rows()
    {
        var t1 = store("wolverinedb://sqlserver/server/t1/wolverine", MessageStoreRole.Tenant);
        var main = tenanted(theMain, t1);

        new Envelope { TenantId = "t1" }.InboxRowIsIn(t1, main).ShouldBeTrue();
        new Envelope { Store = main, TenantId = "t1" }.InboxRowIsIn(t1, main).ShouldBeTrue();
    }

    [Fact]
    public void tenant_database_of_one_ancillary_store_does_not_hold_another_ancillary_stores_row()
    {
        // GH-5731 with RegisterTenants: A and B both register tenant t1, in databases A1 and B1
        var a1 = store("wolverinedb://sqlserver/server/a1/wolverine", MessageStoreRole.Tenant);
        var b1 = store("wolverinedb://sqlserver/server/b1/wolverine", MessageStoreRole.Tenant);
        var tenantedA = tenanted(storeA, a1);
        var tenantedB = tenanted(storeB, b1);

        var envelope = new Envelope { Store = tenantedA, TenantId = "t1" };

        envelope.InboxRowIsIn(a1, theMain).ShouldBeTrue();
        envelope.InboxRowIsIn(b1, theMain).ShouldBeFalse();
        envelope.InboxRowIsIn(tenantedB, theMain).ShouldBeFalse();
        envelope.InboxRowIsIn(tenantedA, theMain).ShouldBeTrue();
    }
}
