using CoreTests.Runtime;
using JasperFx.Core;
using JasperFx.Descriptors;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

// GH-4267. Every FindAllAsync() used to re-enumerate the tenant database list, and the paths that call it
// are retried on failure, so on a large tenant fleet the retry for a connection failure opened another
// connection to the tenant registry to look the databases up again.
public class throttling_the_tenant_database_list_refresh
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly MessageStoreCollection theStores;

    public throttling_the_tenant_database_list_refresh()
    {
        theSource.Cardinality.Returns(DatabaseCardinality.DynamicMultiple);
        theSource.AllActive().Returns(Array.Empty<IMessageStore>());

        var main = Substitute.For<IMessageStore>();
        main.Uri.Returns(new Uri("wolverinedb://fake/main"));
        main.Role.Returns(MessageStoreRole.Main);

        var multiTenanted = new MultiTenantedMessageStore(main, theRuntime, theSource);

        theStores = new MessageStoreCollection(theRuntime, [multiTenanted], []);
    }

    [Fact]
    public async Task concurrent_callers_share_a_single_refresh()
    {
        var refreshIsRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theSource.RefreshAsync().Returns(refreshIsRunning.Task);

        // The first caller starts the refresh and blocks on it; the rest arrive while it is still running
        // and have to join it rather than start their own.
        var first = theStores.FindAllAsync();
        var joiners = Enumerable.Range(0, 19).Select(_ => theStores.FindAllAsync().AsTask()).ToArray();

        await theSource.Received(1).RefreshAsync();

        refreshIsRunning.SetResult();

        await first;
        await Task.WhenAll(joiners);

        await theSource.Received(1).RefreshAsync();
    }

    [Fact]
    public async Task a_second_enumeration_inside_the_stale_window_does_not_ask_again()
    {
        theRuntime.Options.Durability.TenantDatabaseListStaleTime = 30.Seconds();
        theSource.RefreshAsync().Returns(Task.CompletedTask);

        await theStores.FindAllAsync();
        await theStores.FindAllAsync();
        await theStores.FindAllAsync();

        await theSource.Received(1).RefreshAsync();
    }

    [Fact]
    public async Task the_list_is_refreshed_again_once_the_stale_time_has_elapsed()
    {
        theRuntime.Options.Durability.TenantDatabaseListStaleTime = TimeSpan.Zero;
        theSource.RefreshAsync().Returns(Task.CompletedTask);

        await theStores.FindAllAsync();
        await theStores.FindAllAsync();

        await theSource.Received(2).RefreshAsync();
    }

    [Fact]
    public async Task a_lookup_that_misses_forces_past_the_stale_window()
    {
        // GH-4267 follow-up. FindDatabaseAsync refreshes precisely BECAUSE it could not find the database,
        // so a list the window is vouching for is the one answer it must not accept: the caller is asking
        // about a database that, by construction, is not in that list. Left throttled, a tenant database
        // provisioned moments ago is invisible to a single-database lookup until the window elapses -- and
        // BuildAgentAsync turns that null into "No database with Uri ... supports a durability agent".
        theRuntime.Options.Durability.TenantDatabaseListStaleTime = 30.Seconds();
        theSource.RefreshAsync().Returns(Task.CompletedTask);

        // Opens the window.
        await theStores.FindAllAsync();
        await theSource.Received(1).RefreshAsync();

        // Well inside it, and a miss.
        (await theStores.FindDatabaseAsync(new Uri("wolverinedb://fake/provisioned-a-moment-ago")))
            .ShouldBeNull();

        await theSource.Received(2).RefreshAsync();
    }

    [Fact]
    public async Task a_forced_lookup_still_joins_a_refresh_already_under_way()
    {
        // Forcing skips the freshness check, NOT the single-flight guard. That guard is the half that
        // closes the connection storm -- every concurrent caller and every retry opening its own
        // connection to the registry -- so a miss must never be a licence to open another one.
        theRuntime.Options.Durability.TenantDatabaseListStaleTime = 30.Seconds();

        var refreshIsRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theSource.RefreshAsync().Returns(refreshIsRunning.Task);

        var enumerating = theStores.FindAllAsync();
        var missing = Enumerable
            .Range(0, 12)
            .Select(i => theStores.FindDatabaseAsync(new Uri($"wolverinedb://fake/missing-{i}")).AsTask())
            .ToArray();

        await theSource.Received(1).RefreshAsync();

        refreshIsRunning.SetResult();

        await enumerating;
        await Task.WhenAll(missing);

        await theSource.Received(1).RefreshAsync();
    }

    [Fact]
    public async Task a_failed_refresh_is_retried_by_the_next_caller()
    {
        theRuntime.Options.Durability.TenantDatabaseListStaleTime = 30.Seconds();
        theSource.RefreshAsync().Returns(
            Task.FromException(new TimeoutException("the operation has timed out")),
            Task.CompletedTask);

        await Should.ThrowAsync<TimeoutException>(async () => await theStores.FindAllAsync());

        // A failure leaves the list unknown, so the window must not have opened.
        await theStores.FindAllAsync();

        await theSource.Received(2).RefreshAsync();
    }
}
