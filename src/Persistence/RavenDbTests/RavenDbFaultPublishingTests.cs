using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.TestDriver;
using Wolverine;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Util;

namespace RavenDbTests;

/// <summary>
/// Compliance derivation for the Wolverine.RavenDb durable store.
///
/// RavenTestDriver is itself a class (it manages an embedded RavenDB server) and
/// DurableFaultPublishingCompliance is also a class — so single inheritance forces
/// us to host the compliance via composition. The bridge below adapts back to the
/// abstract base so both [Fact]s in DurableFaultPublishingCompliance run as
/// owned facts of this class.
/// </summary>
public class RavenDbFaultPublishingTests : RavenTestDriver
{
    private sealed class Bridge : DurableFaultPublishingCompliance
    {
        private readonly RavenDbFaultPublishingTests _parent;

        public Bridge(RavenDbFaultPublishingTests parent) => _parent = parent;

        public override Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null)
            => _parent.BuildCleanHostAsync(optionalCompose);

        protected override Task<DurableSnapshot> SnapshotAsync(IHost host)
            => _parent.SnapshotAsync(host);
    }

    public Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null)
    {
        DatabaseFixture.EnsureServerConfigured();

        // RavenTestDriver provisions a uniquely-named database per call, so each
        // host gets a clean store and we don't need an explicit clear step.
        var store = GetDocumentStore();

        return BuildHostAsync(store, optionalCompose);
    }

    private static async Task<IHost> BuildHostAsync(IDocumentStore store, Action<WolverineOptions>? optionalCompose)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.KeepAfterMessageHandling = 5.Minutes();

                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton(store);

                opts.Discovery.IncludeType<AlwaysFailsHandler>();
                opts.Discovery.IncludeType<FaultSinkHandler>();
                opts.Services.AddSingleton<FaultSink>();

                opts.OnException<Exception>().MoveToErrorQueue();
                opts.PublishFaultEvents();
                opts.Policies.UseDurableLocalQueues();

                optionalCompose?.Invoke(opts);
            }).StartAsync();
    }

    public async Task<DurableSnapshot> SnapshotAsync(IHost host)
    {
        var documentStore = host.Services.GetRequiredService<IDocumentStore>();

        // Make sure the embedded server has flushed indexes before we count.
        WaitForIndexing(documentStore);

        using var session = documentStore.OpenAsyncSession();

        var dlqCount = await session.Query<DeadLetterMessage>().CountAsync();

        // The Fault<T> envelope is persisted via the inbox for an in-process durable
        // local queue (UseDurableLocalQueues) and via the outbox for a remote durable
        // destination. Sum both so the atomicity assertion holds in either topology.
        var faultTypeName = typeof(Fault<OrderPlaced>).ToMessageTypeName();
        var outgoingFaultCount = await session
            .Query<OutgoingMessage>()
            .Where(o => o.MessageType == faultTypeName)
            .CountAsync();
        var incomingFaultCount = await session
            .Query<IncomingMessage>()
            .Where(i => i.MessageType == faultTypeName)
            .CountAsync();

        return new DurableSnapshot(dlqCount, outgoingFaultCount + incomingFaultCount);
    }

    [Fact]
    public Task smoke_durable_terminal_failure_publishes_fault()
        => new Bridge(this).smoke_durable_terminal_failure_publishes_fault();

    [Fact]
    public Task happy_path_persists_both_dlq_and_fault_rows()
        => new Bridge(this).happy_path_persists_both_dlq_and_fault_rows();
}
