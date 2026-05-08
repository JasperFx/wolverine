using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Wolverine.Attributes;
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
        options.CodeGeneration.InsertFirstPersistenceStrategy<RavenDbPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new AsyncDocumentSessionSource());
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