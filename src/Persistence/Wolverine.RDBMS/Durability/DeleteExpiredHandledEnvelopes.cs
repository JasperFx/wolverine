using System;
using System.Threading.Tasks;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

public class DeleteExpiredHandledEnvelopes : IDurabilityAction
{
    public string Description => "Deleting Expired, Handled Envelopes";

    public Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent, AdvancedSettings nodeSettings,
        DatabaseSettings databaseSettings)
    {
        return storage.Session.WithinTransactionAsync(() => storage.DeleteExpiredHandledEnvelopesAsync(DateTimeOffset.UtcNow));
    }
}