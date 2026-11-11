using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;
using Raven.TestDriver;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Transports.Tcp;

namespace RavenDbTests;

public class DatabaseFixture : RavenTestDriver
{
    private static bool _configured;

    public IDocumentStore StartRavenStore()
    {
        EnsureServerConfigured();
        return GetDocumentStore();
    }

    internal static void EnsureServerConfigured()
    {
        if (_configured) return;
        _configured = true;

        // Configure the embedded RavenDB server.
        // RavenDB.TestDriver 7.0.x requires .NET 8.0.15+ runtime.
        // We try to use a brew-installed .NET 8 if available, otherwise fall back to system dotnet.
        var options = new TestServerOptions
        {
            FrameworkVersion = null, // Use available runtime
            Licensing = new ServerOptions.LicensingOptions
            {
                ThrowOnInvalidOrMissingLicense = false // Don't require license for tests
            }
        };

        // Check for brew-installed .NET 8 which has newer runtime
        var brewDotNetPath = "/opt/homebrew/opt/dotnet@8/bin/dotnet";
        if (File.Exists(brewDotNetPath))
        {
            options.DotNetPath = brewDotNetPath;
        }

        ConfigureServer(options);
    }
}

[CollectionDefinition("raven")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

[Collection("raven")]
public class message_store_compliance : MessageStoreCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;

    public message_store_compliance(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        var store = _fixture.StartRavenStore();
        _store = store;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // TODO -- TEMP!
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton<IDocumentStore>(store);

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();

        return host;
    }

    [Fact]
    public async Task marks_envelope_as_having_an_expires_on_mark_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        using var session = _store.OpenAsyncSession();
        var incoming = await session.LoadAsync<IncomingMessage>(envelope.Id.ToString(), TestContext.Current.CancellationToken);
        var metadata = session.Advanced.GetMetadataFor(incoming);
        metadata.TryGetValue("@expires", out var raw).ShouldBeTrue();

        var value = metadata["@expires"];
        Debug.WriteLine(value);

    }

    // bulk_store_with_intra_batch_duplicate_throws_DuplicateIncomingEnvelopeException lived here (the ASB on-startup race
    // where the same envelope arrived twice in one prefetched batch and RavenDB's NonUniqueObjectException leaked). It is
    // a MessageStoreCompliance fact for every store now, GH-5123.

    [Fact]
    public async Task does_not_save_earlier_claims_if_a_later_candidate_fails_to_load()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = Wolverine.Transports.TransportConstants.AnyNode;
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var candidate = (await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(envelope.Destination!, 10))
            .Single(x => x.Id == envelope.Id);
        var store = (RavenDbMessageStore)thePersistence;

        await Should.ThrowAsync<NullReferenceException>(() => store.TryClaimIncomingAsync(45_001,
            new Envelope[] { candidate, null! }));

        using var session = _store.OpenAsyncSession();
        var persisted = await session.LoadAsync<IncomingMessage>(store.IdentityFor(envelope),
            TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        persisted.OwnerId.ShouldBe(Wolverine.Transports.TransportConstants.AnyNode);
    }

    [Fact]
    public async Task node_persistence_works_when_store_has_optimistic_concurrency_enabled()
    {
        // Node-agent persistence has two failure modes when the consumer enables optimistic
        // concurrency on the document store:
        //   1. PersistAsync and PersistAgentRestrictionsAsync use cluster-wide transactions,
        //      which RavenDB rejects in combination with optimistic concurrency.
        //   2. AddAssignmentAsync and AssignAgentsAsync used to write a brand-new
        //      agent-assignment document by id, which fails when re-electing an agent
        //      whose document still exists from a prior run.
        using var optimisticStore = new DocumentStore
        {
            Urls = _store.Urls,
            Database = "wolverine-optimistic-concurrency-test-" + Guid.NewGuid()
        };
        optimisticStore.Conventions.UseOptimisticConcurrency = true;
        optimisticStore.Initialize();
        await optimisticStore.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(optimisticStore.Database)), TestContext.Current.CancellationToken);

        var ravenStore = new RavenDbMessageStore(optimisticStore, new WolverineOptions());

        var node = new Wolverine.Runtime.Agents.WolverineNode { NodeId = Guid.NewGuid() };
        var assigned = await ravenStore.PersistAsync(node, CancellationToken.None);
        assigned.ShouldBe(1);

        await ravenStore.PersistAgentRestrictionsAsync(
            new[] { new Wolverine.Runtime.Agents.AgentRestriction(Guid.NewGuid(), new Uri("wolverine://test"), Wolverine.Runtime.Agents.AgentRestrictionType.Pinned, 1) },
            CancellationToken.None);

        // Re-adding the same assignment must succeed — assignments are idempotent.
        var agentUri = new Uri("wolverine://agents/leader");
        await ravenStore.AddAssignmentAsync(node.NodeId, agentUri, CancellationToken.None);
        await ravenStore.AddAssignmentAsync(node.NodeId, agentUri, CancellationToken.None);

        await ravenStore.AssignAgentsAsync(node.NodeId, new[] { agentUri }, CancellationToken.None);
    }


}
