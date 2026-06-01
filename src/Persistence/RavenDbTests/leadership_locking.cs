using JasperFx.Core.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

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
        // server-side entry - that's the contract the heartbeat relies on to
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

    [Fact]
    public async Task raw_compare_exchange_exclusivity_proof()
    {
        // Direct RavenDB CompareExchange test - no Wolverine wrapping.
        // Proves PutCompareExchangeValueOperation with index=0 is truly exclusive.
        var key = "test/exclusivity/" + Guid.NewGuid();
        var lock1 = new DistributedLock { NodeId = Guid.NewGuid(), ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var lock2 = new DistributedLock { NodeId = Guid.NewGuid(), ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };

        // First acquisition - should succeed (key doesn't exist)
        var firstPut = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lock1, 0));
        firstPut.Successful.ShouldBeTrue("First acquisition with index=0 must succeed");

        // Second acquisition with index=0 on same key - must FAIL if CE is exclusive
        var secondPut = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lock2, 0));
        secondPut.Successful.ShouldBeFalse(
            "Second acquisition with index=0 on same key must fail - CompareExchange is exclusive");
        secondPut.Value.ShouldNotBeNull();
        secondPut.Value.NodeId.ShouldBe(lock1.NodeId, "Existing value should still be lock1's node");

        // Correct-index acquisition (using the index from first put) - should succeed
        var correctIndexPut = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lock2, firstPut.Index));
        correctIndexPut.Successful.ShouldBeTrue("Acquisition with correct index must succeed");

        // Wrong-index delete - should FAIL
        var wrongIndex = correctIndexPut.Index + 999;
        var wrongDelete = await _store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DistributedLock>(key, wrongIndex));
        wrongDelete.Successful.ShouldBeFalse("Delete with wrong index must fail");

        // Correct-index delete - should succeed
        var correctDelete = await _store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DistributedLock>(key, correctIndexPut.Index));
        correctDelete.Successful.ShouldBeTrue("Delete with correct index must succeed");
    }

    [Fact]
    public async Task stale_index_causes_lock_renewal_failure()
    {
        // Proves: when two callers concurrently read the same server-side
        // lock index, one renewal succeeds (bumping the index) and the other
        // fails (using the stale index). Wolverine interprets the failed
        // renewal as "leadership lock lost" and calls stepDownAsync, which
        // tries to delete the lock value using the stale index. This delete
        // ALSO fails because the index has changed. The lock value survives
        // under the winning caller's index - orphaned. This is the exact
        // mechanism that causes the split-brain / leadership timeout.

        var key = "test/renewal-race/" + Guid.NewGuid();
        var lockVal1 = new DistributedLock { NodeId = Guid.NewGuid(), ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };

        // Acquire initially -> index becomes N
        var create = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lockVal1, 0));
        create.Successful.ShouldBeTrue();
        var expectedIndex = create.Index;

        // Now simulate TWO concurrent callers both reading _lastLockIndex=N
        // Caller 1 renews first -> index becomes N+1, _lastLockIndex updated
        // Caller 2 uses stale N -> PUT fails

        // - Caller 1 (correct index) succeeds -
        var lockVal2 = new DistributedLock { NodeId = Guid.NewGuid(), ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var caller1 = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lockVal2, expectedIndex));
        caller1.Successful.ShouldBeTrue("Caller 1 renewal with correct index succeeds");
        var afterCaller1Index = caller1.Index;

        // - Caller 2 (stale index N, but actual index is now N+1) fails -
        var lockVal3 = new DistributedLock { NodeId = Guid.NewGuid(), ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var caller2 = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lockVal3, expectedIndex));
        caller2.Successful.ShouldBeFalse("Caller 2 with stale index MUST fail - proves race causes renewal failure");

        // - What Wolverine does: stepDownAsync -> ReleaseLeadershipLockAsync
        //     which deletes the lock value using its stale _lastLockIndex -
        //     This delete ALSO fails because index is wrong!
        var staleDelete = await _store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DistributedLock>(key, expectedIndex));
        staleDelete.Successful.ShouldBeFalse(
            "Delete with stale index fails - lock value remains, so another node can acquire it via take-over");

        // - Cleanup: delete with correct index -
        var cleanDelete = await _store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DistributedLock>(key, afterCaller1Index));
        cleanDelete.Successful.ShouldBeTrue("Cleanup delete with correct index succeeds");
    }

    [Fact]
    public async Task concurrent_lock_renewal_race_orphans_the_lock()
    {
        // Simulates the exact race in the leader election test:
        //   executeHealthChecks loop and CheckAgentHealth message
        //   call DoHealthChecksAsync -> TryAttainLeadershipLockAsync concurrently.
        // Both callers read the same server-side lock index. The first
        // renewal bumps the index; the second uses the stale value and
        // fails. Wolverine then calls stepDownAsync -> ReleaseLeadershipLockAsync
        // with the stale index, which ALSO fails. The lock value survives
        // under the winning index - orphaned. No other node can acquire
        // it (index=0 fails because a value already exists). Only
        // lease-expiry takeover (5-minute timeout) can recover.

        var key = "test/concurrent-race/" + Guid.NewGuid();
        var owner = Guid.NewGuid();
        var initialLock = new DistributedLock { NodeId = owner, ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };

        // === Step 1: Host1 acquires the lock (index=N) ===
        var create = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, initialLock, 0));
        create.Successful.ShouldBeTrue();
        var sharedIndex = create.Index;   // This is like _lastLockIndex on Host1
        Console.WriteLine($"Step 1: Acquired lock, index={sharedIndex}");

        // === Step 2: TWO concurrent renewal callers both read sharedIndex=N ===
        // Caller A is the health check loop
        // Caller B is CheckAgentHealth message processing
        var lockA = new DistributedLock { NodeId = owner, ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var lockB = new DistributedLock { NodeId = owner, ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5) };
        var resultA = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lockA, sharedIndex));
        var resultB = await _store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DistributedLock>(key, lockB, sharedIndex));

        // Exactly one succeeds (the one whose request wins the network race)
        // The other fails because the lock index was bumped by the first
        resultA.Successful.ShouldNotBe(resultB.Successful,
            "Exactly one concurrent renewal must succeed - proves the race on the shared index");

        // The FAILING caller simulates what happens in stepDownAsync:
        // it tries to delete the lock value using its stale sharedIndex
        // This delete FAILS because the lock value has a new index
        var staleDelete = await _store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DistributedLock>(key, sharedIndex));
        staleDelete.Successful.ShouldBeFalse();

        // === Step 3: Cleanup - delete with actual current index ===
        var currentIndex = resultA.Successful ? resultA.Index : resultB.Index;
        var cleanDelete = await _store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<DistributedLock>(key, currentIndex));
        cleanDelete.Successful.ShouldBeTrue("Cleanup delete succeeds");
    }

    [Fact]
    public async Task concurrent_DoHealthChecksAsync_guard_prevents_spurious_stepdown()
    {
        // Wolverine-level race test: the Interlocked guard in
        // NodeAgentController.DoHealthChecksAsync must prevent concurrent
        // health check execution. Without the guard, two callers race on
        // _lastLockIndex in TryAttainLeadershipLockAsync — one renewal
        // succeeds, the other fails, triggering stepDownAsync and leadership
        // loss. With the guard, the second caller returns Empty immediately
        // and the leader stays leader.

        using var balancedHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.HealthCheckPollingTime = 10.Minutes();
                opts.Durability.CheckAssignmentPeriod = 10.Minutes();
                opts.Durability.FirstHealthCheckExecution = 10.Minutes();
                opts.ServiceName = "race-test";
                opts.UseRavenDbPersistence();
                opts.UseTcpForControlEndpoint();
            }).StartAsync();

        var runtime = balancedHost.GetRuntime();
        await runtime.DoHealthChecksAsync();
        runtime.IsLeader().ShouldBeTrue("Host must be leader BEFORE");
        runtime.Storage.Nodes.HasLeadershipLock().ShouldBeTrue("Leadership lock must be true BEFORE");

        await Task.WhenAll(Enumerable.Repeat(1, 10).Select(_ => runtime.DoHealthChecksAsync()));

        runtime.IsLeader().ShouldBeTrue("Host must be leader AFTER");
        runtime.Storage.Nodes.HasLeadershipLock().ShouldBeTrue("Leadership lock must be true AFTER");
    }
}
