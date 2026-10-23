using CoreTests.Runtime;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-5171. With a database per tenant, the durable sending agent's outbox coalescers used to be
/// shared by every tenant sending to the same destination, one flush at a time. One tenant's slow
/// database -- an exhausted connection pool, say -- then held up every other tenant's outbox writes
/// and deletes, and so every other tenant's request. Each tenant database now gets its own lane.
/// </summary>
public class per_tenant_outbox_coalescing_5171 : IAsyncDisposable
{
    private readonly IMessageStore theMainStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theSlowStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theFastStore = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly MultiTenantedMessageStore theStore;

    private readonly Uri theDestination = new("stub://one");
    private readonly ISender theSender = Substitute.For<ISender>();
    private DurableSendingAgent? _agent;

    public per_tenant_outbox_coalescing_5171()
    {
        var runtime = new MockWolverineRuntime();

        // Keep GetDatabaseAsync from trying to migrate a substitute on first resolution
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        theSource.FindAsync("slow").Returns(theSlowStore);
        theSource.FindAsync("fast").Returns(theFastStore);

        theStore = new MultiTenantedMessageStore(theMainStore, runtime, theSource);

        theSender.Destination.Returns(theDestination);
        theSender.SupportsNativeScheduledSend.Returns(true);
    }

    private DurableSendingAgent agent()
    {
        // The default batch size, which is what turns the coalescers on
        var settings = new DurabilitySettings { StoreOutgoingBatchSize = 100 };
        _agent = new DurableSendingAgent(theSender, settings, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), theStore, new StubEndpoint(theDestination));
        return _agent;
    }

    public async ValueTask DisposeAsync()
    {
        if (_agent != null) await _agent.DisposeAsync();
    }

    private Envelope envelopeFor(string tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        envelope.Destination = theDestination;
        return envelope;
    }

    /// <summary>
    /// Every write to the slow tenant's database waits until the test lets it go.
    /// </summary>
    private TaskCompletionSource slowDownTheSlowTenant()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        theSlowStore.Outbox.DeleteOutgoingAsync(Arg.Any<Envelope>()).Returns(_ => gate.Task);
        theSlowStore.Outbox.DeleteOutgoingAsync(Arg.Any<Envelope[]>()).Returns(_ => gate.Task);
        theSlowStore.Outbox.StoreOutgoingAsync(Arg.Any<Envelope>(), Arg.Any<int>()).Returns(_ => gate.Task);
        theSlowStore.Outbox.StoreOutgoingAsync(Arg.Any<IReadOnlyList<Envelope>>(), Arg.Any<int>())
            .Returns(_ => gate.Task);

        return gate;
    }

    [Fact]
    public async Task a_slow_tenant_database_does_not_delay_another_tenants_successful_send()
    {
        var theAgent = agent();
        var gate = slowDownTheSlowTenant();

        // The slow tenant's delete is in flight, and more of its deletes pile up behind it
        var slowDeletes = Enumerable.Range(0, 3)
            .Select(_ => theAgent.MarkSuccessfulAsync(envelopeFor("slow")))
            .ToArray();

        // The other tenant's request is waiting on this to return
        var fast = envelopeFor("fast");
        await theAgent.MarkSuccessfulAsync(fast)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await theFastStore.Outbox.Received(1).DeleteOutgoingAsync(fast);
        slowDeletes.ShouldAllBe(x => !x.IsCompleted);

        // And the slow tenant still gets all of its rows deleted once its database catches up
        gate.SetResult();
        await Task.WhenAll(slowDeletes).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_slow_tenant_database_does_not_delay_another_tenants_outbox_store()
    {
        var theAgent = agent();
        var gate = slowDownTheSlowTenant();

        var slowStores = Enumerable.Range(0, 3)
            .Select(_ => theAgent.TryStoreOutgoingAsync(envelopeFor("slow")).AsTask())
            .ToArray();

        var fast = envelopeFor("fast");
        (await theAgent.TryStoreOutgoingAsync(fast).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await theFastStore.Outbox.Received(1).StoreOutgoingAsync(fast, Arg.Any<int>());
        slowStores.ShouldAllBe(x => !x.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(slowStores).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task tenants_that_share_a_database_still_share_a_batch()
    {
        // Two tenant ids, one database behind them
        theSource.FindAsync("fast-too").Returns(theFastStore);
        await theStore.GetDatabaseAsync("fast");
        await theStore.GetDatabaseAsync("fast-too");

        var theAgent = agent();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        theFastStore.Outbox.DeleteOutgoingAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            if (!first) return Task.CompletedTask;
            first = false;
            started.TrySetResult();
            return gate.Task;
        });

        var lead = theAgent.MarkSuccessfulAsync(envelopeFor("fast"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var others = new[] { envelopeFor("fast"), envelopeFor("fast-too"), envelopeFor("fast-too") };
        var pending = others.Select(theAgent.MarkSuccessfulAsync).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(lead))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await theFastStore.Outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(x => x.Length == 3 && others.All(x.Contains)));
    }

    [Fact]
    public void a_single_database_outbox_keeps_the_single_lane_coalescer()
    {
        // No per-envelope partition lookup at all when there is only one database to write to
        EnvelopeStoreCoalescing.PartitionerFor(Substitute.For<IMessageOutbox>()).ShouldBeNull();
        EnvelopeStoreCoalescing.PartitionerFor(Substitute.For<IMessageInbox>()).ShouldBeNull();

        EnvelopeStoreCoalescing.Build(null, _ => Task.CompletedTask, _ => Task.CompletedTask, 100, theDestination,
            NullLogger.Instance).ShouldBeOfType<EnvelopeStoreCoalescer>();
    }

    [Fact]
    public async Task the_default_tenant_and_the_main_store_share_one_lane()
    {
        await theStore.GetDatabaseAsync("fast");

        theStore.CoalescingPartitionFor(null).ShouldBeSameAs(theMainStore);
        theStore.CoalescingPartitionFor(StorageConstants.DefaultTenantId).ShouldBeSameAs(theMainStore);
        theStore.CoalescingPartitionFor(TransportConstants.Default).ShouldBeSameAs(theMainStore);
        theStore.CoalescingPartitionFor("fast").ShouldBeSameAs(theFastStore);

        // A tenant nobody has resolved yet gets a lane by its id until its store is known
        theStore.CoalescingPartitionFor("unseen").ShouldBe("unseen");
    }

    private class StubEndpoint : Endpoint
    {
        public StubEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

        protected override bool supportsMode(EndpointMode mode) => true;
    }
}
