using CoreTests.Runtime;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

// Regression for #3124's sibling teardown bug #3123: when teardown runs after a failed/partial
// startup, an ancillary store whose schema was never created throws (e.g. PostgreSQL 42P01) from
// ReleaseAllOwnershipAsync. That must not abort releasing the OTHER stores, nor surface as an
// unhandled teardown exception that masks the original startup failure.
public class release_all_ownership_is_best_effort
{
    private static IMessageStore StoreFor(string uri, MessageStoreRole role, IMessageStoreAdmin admin)
    {
        var store = Substitute.For<IMessageStore>();
        store.Uri.Returns(new Uri(uri));
        store.Role.Returns(role);
        store.Admin.Returns(admin);
        return store;
    }

    [Fact]
    public async Task one_failing_store_does_not_stop_the_others_or_throw()
    {
        var failingAdmin = Substitute.For<IMessageStoreAdmin>();
        failingAdmin.ReleaseAllOwnershipAsync(Arg.Any<int>())
            .Returns(Task.FromException(new InvalidOperationException("42P01: relation does not exist")));

        var healthyAdmin = Substitute.For<IMessageStoreAdmin>();
        healthyAdmin.ReleaseAllOwnershipAsync(Arg.Any<int>()).Returns(Task.CompletedTask);

        var failing = StoreFor("wolverinedb://fake/ancillary-without-schema", MessageStoreRole.Ancillary, failingAdmin);
        var healthy = StoreFor("wolverinedb://fake/main", MessageStoreRole.Ancillary, healthyAdmin);

        var collection = new MessageStoreCollection(new MockWolverineRuntime(), [failing, healthy], []);

        // Must not throw even though one store's release fails
        await Should.NotThrowAsync(() => collection.ReleaseAllOwnershipAsync(5));

        // And the healthy store must still have had its ownership released
        await healthyAdmin.Received().ReleaseAllOwnershipAsync(5);
    }
}
