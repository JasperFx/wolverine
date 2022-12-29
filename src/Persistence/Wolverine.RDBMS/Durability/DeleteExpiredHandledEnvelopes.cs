using System;
using System.Threading.Tasks;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

public class DeleteExpiredHandledEnvelopes : IDurabilityAction
{
    public string Description { get; } = "Deleting Expired, Handled Envelopes";

    public async Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent)
    {
        await storage.Session.BeginAsync();


        try
        {
            await storage.DeleteExpiredHandledEnvelopesAsync(DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            await storage.Session.RollbackAsync();
            throw;
        }

        await storage.Session.CommitAsync();
    }
}