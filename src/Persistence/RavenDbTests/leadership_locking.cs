using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Embedded;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

[Collection("raven")]
public class leadership_locking : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public leadership_locking(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _store = _fixture.StartRavenStore();
        _host = await buildHost();
    }

    private async Task<IHost> buildHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "locking";
                opts.UseRavenDbPersistence();

            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task try_to_lock_happy_path()
    {
        var store = _host.Services.GetService<IMessageStore>()!;
        store.Nodes.HasLeadershipLock().ShouldBeFalse();

        var gotLock = await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None);
        gotLock.ShouldBeTrue();

        store.Nodes.HasLeadershipLock().ShouldBeTrue();

        await store.Nodes.ReleaseLeadershipLockAsync();

        store.Nodes.HasLeadershipLock().ShouldBeFalse();
    }

    [Fact]
    public async Task lock_is_exclusive()
    {
        using var host2 = await buildHost();
        var store2 = host2.Services.GetService<IMessageStore>()!;

        var store = _host.Services.GetService<IMessageStore>()!;
        store.Nodes.HasLeadershipLock().ShouldBeFalse();

        var gotLock = await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None);
        gotLock.ShouldBeTrue();

        store.Nodes.HasLeadershipLock().ShouldBeTrue();

        (await store2.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None))
            .ShouldBeFalse();
        store2.Nodes.HasLeadershipLock().ShouldBeFalse();

        await store.Nodes.ReleaseLeadershipLockAsync();
        
        store.Nodes.HasLeadershipLock().ShouldBeFalse();
        
        (await store2.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None))
            .ShouldBeTrue();
        store2.Nodes.HasLeadershipLock().ShouldBeTrue();
    }

    [Fact]
    public async Task expired_scheduled_job_lock_from_dead_predecessor_can_be_taken_over()
    {
        // Simulate a prior process that crashed without releasing — leaves a CE value
        // with an ExpirationTime in the past. A fresh process should take it over
        // rather than be blocked indefinitely.
        var staleLock = new DistributedLock
        {
            NodeId = Guid.NewGuid(),
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var put = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>("wolverine/scheduled", staleLock, 0));
        put.Successful.ShouldBeTrue();

        // Build a brand-new message store with no in-memory lock state — mirrors a
        // fresh process starting up against the predecessor's leftover CE value.
        var ravenStore = new RavenDbMessageStore(_store, new WolverineOptions());
        (await ravenStore.TryAttainScheduledJobLockAsync(CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task try_attain_renews_the_server_side_lease_when_already_held()
    {
        // Calling TryAttain when the lease is already held must refresh the
        // server-side entry — that's the contract the heartbeat relies on to
        // keep the lease alive.
        var store = _host.Services.GetService<IMessageStore>()!.As<RavenDbMessageStore>();
        var lockId = "wolverine/leader/locking";

        (await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();
        var initial = await _store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DistributedLock>(lockId));

        await Task.Delay(10);

        (await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();
        var renewed = await _store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DistributedLock>(lockId));

        renewed.Index.ShouldBeGreaterThan(initial.Index);
        renewed.Value.ExpirationTime.ShouldBeGreaterThan(initial.Value.ExpirationTime);

        await store.Nodes.ReleaseLeadershipLockAsync();
    }
}