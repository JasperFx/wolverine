using System;
using System.Threading.Tasks;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

public class MoveReplayableErrorMessagesToIncoming : IDurabilityAction
{
    public string Description => "Moving Replayable Error Envelopes from DeadLetterTable to IncomingTable";

    public Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent,
        IDurableStorageSession session)
    {
        return session.WithinTransactionAsync(() => MoveReplayableErrorMessagesToIncomingAsync(session, database.Settings));
    }
    
    public Task MoveReplayableErrorMessagesToIncomingAsync(IDurableStorageSession session, DatabaseSettings databaseSettings)
    {
        if (session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }
        
        var insertIntoIncomingSql = $"insert into {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} ({DatabaseConstants.IncomingFields})" +
                                    $" select {DatabaseConstants.Body}, {DatabaseConstants.Id}, '{EnvelopeStatus.Incoming}', 0, null, 0, {DatabaseConstants.MessageType}, {DatabaseConstants.ReceivedAt} " +
                                    $"from {databaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable} " +
                                    $"where {DatabaseConstants.Replayable} = @replayable";

        var removeFromDeadLetterSql = $"; delete from {databaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable} where {DatabaseConstants.Replayable} = @replayable";
        
        var removeFromDeadLetterSqlAndInsertIntoIncomingSql  = $"{insertIntoIncomingSql}; {removeFromDeadLetterSql}";
        
        return session.CreateCommand(removeFromDeadLetterSqlAndInsertIntoIncomingSql)
            .With("replayable", true)
            .ExecuteNonQueryAsync(session.Cancellation);
    }
}