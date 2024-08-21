using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageStoreAdmin
{
    public async Task ClearAllAsync()
    {
        throw new NotImplementedException();
    }

    public async Task RebuildAsync()
    {
        throw new NotImplementedException();
    }

    public async Task<PersistedCounts> FetchCountsAsync()
    {
        throw new NotImplementedException();
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        throw new NotImplementedException();
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        throw new NotImplementedException();
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        throw new NotImplementedException();
    }

    public async Task ReleaseAllOwnershipAsync(int ownerId)
    {
        throw new NotImplementedException();
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public async Task MigrateAsync()
    {
        throw new NotImplementedException();
    }
}