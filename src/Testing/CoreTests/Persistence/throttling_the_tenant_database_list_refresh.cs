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
