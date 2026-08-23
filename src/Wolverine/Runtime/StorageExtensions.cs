using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public static class StorageExtensions
{
    /// <summary>
    /// Test-support reset for the complete Wolverine storage footprint of this application: rebuilds the
    /// envelope storage schema of every known message store (the main store, every tenant database, and
    /// every ancillary store) <i>and</i> leaves every database-backed queue transport's tables built but
    /// empty. Unlike <see cref="IMessageStoreAdmin.ClearAllAsync"/> / <see cref="IMessageStoreAdmin.RebuildAsync"/>,
    /// which only ever touch envelope storage, this reaches the queue transport tables as well so that no
    /// rows carry between integration test runs.
    ///
    /// Safe to call on a host with no message store and no database-backed queues — it simply finds nothing
    /// to do.
    /// </summary>
    /// <param name="host"></param>
    public static async Task ClearAllWolverineStorageAsync(this IHost host)
    {
        var runtime = host.GetRuntime();

        // Envelope storage first. RebuildAsync() migrates the schema and then deletes the envelope rows;
        // it does not drop the schema, so it cannot destroy the queue transport tables handled below.
        foreach (var store in await runtime.Stores.FindAllAsync())
        {
            await store.Admin.RebuildAsync();
        }

        // Then the queue transports whose contents live in storage Wolverine provisions. SetupAsync()
        // builds the queue's storage if it is missing, PurgeAsync() empties it -- and each fans out
        // across every tenant database on a multi-tenanted transport. That pair exists on every such
        // transport (PostgreSQL, SQL Server, MySQL, Oracle, SQLite, and Redis streams), so this needs
        // no provider-specific code.
        //
        // GH-4035: selected by IStorageBackedQueue, NOT by IDatabaseBackedEndpoint. The latter is about
        // inbox integration, and using it here meant GH-4028's (correct) removal of it from the Redis
        // stream endpoint silently dropped Redis streams out of this reset -- with no test able to see
        // it, because no Redis implementation of ClearAllWolverineStorageCompliance existed.
        foreach (var transport in runtime.Options.Transports)
        {
            var queues = transport.Endpoints()
                .OfType<IBrokerQueue>()
                .Where(x => x is IStorageBackedQueue)
                .ToArray();

            if (queues.Length == 0) continue;

            if (transport is IBrokerTransport broker)
            {
                await broker.ConnectAsync(runtime);
            }

            foreach (var queue in queues)
            {
                await queue.SetupAsync(runtime.Logger); // built...
                await queue.PurgeAsync(runtime.Logger); // ...but empty
            }
        }
    }

    /// <summary>
    /// Clear out all the envelope storage for all known message stories in this application
    /// </summary>
    /// <param name="host"></param>
    public static async Task ClearAllEnvelopeStorageAsync(this IHost host)
    {
        var runtime = host.GetRuntime();
        foreach (var store in await runtime.Stores.FindAllAsync())
        {
            await store.Admin.ClearAllAsync();
        }
    }

    /// <summary>
    /// Rebuild the envelope storage for all message stores in this application
    /// </summary>
    /// <param name="host"></param>
    public static async Task RebuildAllEnvelopeStorageAsync(this IHost host)
    {
        var runtime = host.GetRuntime();
        foreach (var store in await runtime.Stores.FindAllAsync())
        {
            await store.Admin.RebuildAsync();
        }
    }

    /// <summary>
    /// Release the ownership of all persisted envelopes by the current node
    /// </summary>
    /// <param name="host"></param>
    public static async Task ReleaseAllOwnershipAsync(this IHost host)
    {
        var runtime = host.GetRuntime();
        foreach (var store in await runtime.Stores.FindAllAsync())
        {
            await store.Admin.ReleaseAllOwnershipAsync();
        }
    }
    
    
}