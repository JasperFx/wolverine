using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
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