using System.Data.Common;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class MoveReplayableErrorMessagesToIncomingOperation : IDatabaseOperation
{
    private readonly IMessageDatabase _database;

    public MoveReplayableErrorMessagesToIncomingOperation(IMessageDatabase database)
    {
        _database = database;
    }

    public string Description => $"Moving Replayable Error Envelopes from {DatabaseConstants.DeadLetterTable} to {DatabaseConstants.IncomingTable}";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"insert into {_database.SchemaName}.{DatabaseConstants.IncomingTable} ({DatabaseConstants.IncomingFields}) ");
        builder.Append("select {DatabaseConstants.Body}, {DatabaseConstants.Id}, '{EnvelopeStatus.Incoming}', 0, null, 0, {DatabaseConstants.MessageType}, {DatabaseConstants.ReceivedAt}");
        builder.Append("from {wolverineDatabase.SchemaName}.{DatabaseConstants.DeadLetterTable} where {DatabaseConstants.Replayable} = @replayable;");
        builder.AddNamedParameter("replayable", true);
        builder.Append("delete from {wolverineDatabase.SchemaName}.{DatabaseConstants.DeadLetterTable} where {DatabaseConstants.Replayable} = @replayable");
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        var number = reader.RecordsAffected;
        
        // TODO -- log something from up above!
        
        // Need to advance the reader because this operation runs two separate queries
        await reader.NextResultAsync(token);
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}