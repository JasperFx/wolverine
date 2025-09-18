using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.Runtime;

public static class StorageExtensions
{
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