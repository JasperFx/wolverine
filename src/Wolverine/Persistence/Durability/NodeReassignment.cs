using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

internal class NodeReassignment : IMessagingAction
{
    private readonly AdvancedSettings _settings;

    public NodeReassignment(AdvancedSettings settings)
    {
        _settings = settings;
    }

    public string Description { get; } = "Dormant node reassignment";

    public async Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent)
    {
        await storage.Session.BeginAsync();

        var gotLock = await storage.Session.TryGetGlobalLockAsync(TransportConstants.ReassignmentLockId);
        if (!gotLock)
        {
            await storage.Session.RollbackAsync();
            return;
        }

        try
        {
            var owners = await storage.FindUniqueOwnersAsync(_settings.UniqueNodeId);

            foreach (var owner in owners.Where(x => x != TransportConstants.AnyNode))
            {
                if (owner == _settings.UniqueNodeId)
                {
                    continue;
                }

                if (await storage.Session.TryGetGlobalTxLockAsync(owner))
                {
                    await storage.ReassignDormantNodeToAnyNodeAsync(owner);
                }
            }
        }
        catch (Exception)
        {
            await storage.Session.RollbackAsync();
            throw;
        }
        finally
        {
            await storage.Session.ReleaseGlobalLockAsync(TransportConstants.ReassignmentLockId);
        }

        await storage.Session.CommitAsync();
    }
}