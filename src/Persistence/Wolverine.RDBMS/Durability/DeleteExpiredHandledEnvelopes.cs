using System;
using System.Threading.Tasks;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

public class DeleteExpiredHandledEnvelopes : IDurabilityAction
{
    public string Description => "Deleting Expired, Handled Envelopes";

    public Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent, NodeSettings nodeSettings,
        DatabaseSettings databaseSettings)
    {
        return storage.Session.WithinTransactionAsync(() => DeleteExpiredHandledEnvelopesAsync(storage.Session, DateTimeOffset.UtcNow, databaseSettings));
    }
    
    public Task DeleteExpiredHandledEnvelopesAsync(IDurableStorageSession session, DateTimeOffset utcNow, DatabaseSettings databaseSettings)
    {
        if (session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }
        
        var sql = $"delete from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' and {DatabaseConstants.KeepUntil} <= @time";


        return session.CreateCommand(sql)
            .With("time", utcNow)
            .ExecuteNonQueryAsync(session.Cancellation);
    }
}