using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RavenDb.Internals;

namespace Wolverine.RavenDb;

public static class WolverineRavenDbExtensions
{
    /// <summary>
    /// Utilize the default RavenDb database for this system for envelope and saga storage
    /// with this system
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static WolverineOptions UseRavenDbPersistence(this WolverineOptions options)
    {
        options.Services.AddSingleton<IMessageStore, RavenDbMessageStore>();

        // Register the native RavenDB control-queue transport eagerly so the
        // "ravendb://" scheme resolves for publishing rules configured at bootstrap.
        // The endpoint only becomes a live listener when the message store promotes
        // it to the NodeControlEndpoint under Balanced durability (see
        // RavenDbMessageStore.Initialize). The store is resolved later, in the
        // transport's InitializeAsync.
        if (!options.Transports.OfType<Internals.Transport.RavenDbControlTransport>().Any())
        {
            options.Transports.Add(new Internals.Transport.RavenDbControlTransport(options));
        }

        options.CodeGeneration.InsertFirstPersistenceStrategy<RavenDbPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new AsyncDocumentSessionSource());

        // GH-4145 (the RavenDb half of GH-3001): prime the service-location child scope with the
        // handler's outbox-enrolled IAsyncDocumentSession so a service-located IAsyncDocumentSession
        // resolves to that same session. The frame self-guards (no-op when the chain has no RavenDb
        // session).
        options.ScopingFrameSources.Add(() =>
            new PrimeScopedSessionFrame<IAsyncDocumentSession, ScopedRavenDocumentSessionHolder>());

        // Unlike Marten / Polecat / Fisher, nothing registers IAsyncDocumentSession in DI -- RavenDb
        // sessions have only ever reached a handler through codegen (AsyncDocumentSessionSource), so a
        // service-located one could not resolve at all. Wolverine owns the registration here: inside a
        // handler scope it is the primed, outbox-enrolled session; everywhere else it is a genuine
        // scoped session off the store. A registration the application made itself is decorated rather
        // than replaced, so its own session-building still runs on the fall-back path.
        options.Services.AddScoped<ScopedRavenDocumentSessionHolder>();
        options.Services.PreferPrimedSession<IAsyncDocumentSession>(
            s => s.GetRequiredService<ScopedRavenDocumentSessionHolder>().Session,
            s => s.GetRequiredService<IDocumentStore>().OpenAsyncSession());

        options.Services.AddHostedService<DeadLetterQueueReplayer>();
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineRavenDbExtensions).Assembly);

        // CritterWatch / saga-explorer diagnostic surface — RavenDb owns
        // every saga whose state is stored in the registered IDocumentStore.
        // The runtime aggregator fans out across all registered
        // ISagaStoreDiagnostics so this lives next to the Marten / EF Core
        // ones for hosts that mix saga storages.
        options.Services.AddSingleton<ISagaStoreDiagnostics>(s =>
            new RavenDbSagaStoreDiagnostics(
                s.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(),
                s.GetRequiredService<IDocumentStore>()));

        return options;
    }

    public static async Task DeleteAllAsync<T>(this IDocumentStore store, string? collectionName = null)
    {
        collectionName ??= typeof(T).Name + "s"; 
        var queryToDelete = new IndexQuery { Query = $"FROM {collectionName}" };
        var operation = await store.Operations.SendAsync(new DeleteByQueryOperation(queryToDelete, new QueryOperationOptions { AllowStale = false }));
        await operation.WaitForCompletionAsync();
    }
}