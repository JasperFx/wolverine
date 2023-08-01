using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Durability;

namespace Wolverine.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Clear out any persisted Wolverine messages and Wolverine node/assignment data. Useful for test automation
    /// </summary>
    /// <param name="host"></param>
    /// <param name="cancellationToken"></param>
    public static async Task ClearAllPersistedWolverineDataAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        var storage = host.Services.GetRequiredService<IMessageStore>();
        await storage.Admin.ClearAllAsync();
        await storage.Nodes.ClearAllAsync(cancellationToken);
    }
}