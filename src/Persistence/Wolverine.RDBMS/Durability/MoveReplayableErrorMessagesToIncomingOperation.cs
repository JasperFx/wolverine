using System.Data.Common;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class MoveReplayableErrorMessagesToIncomingOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly IMessageDatabase _database;

    public MoveReplayableErrorMessagesToIncomingOperation(IMessageDatabase database)
    {
        _database = database;
    }

    public string Description =>
        $"Moving Replayable Error Envelopes from {DatabaseConstants.DeadLetterTable} to {DatabaseConstants.IncomingTable}";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append(
            $"insert into {_database.TableNameFor(DatabaseConstants.IncomingTable)} ({DatabaseConstants.IncomingFields}) ");
        builder.Append(
            $"select {DatabaseConstants.Body}, {DatabaseConstants.Id}, '{EnvelopeStatus.Incoming}', 0, null, 0, {DatabaseConstants.MessageType}, {DatabaseConstants.ReceivedAt}, null ");
        builder.Append(
            $"from {_database.TableNameFor(DatabaseConstants.DeadLetterTable)} where {DatabaseConstants.Replayable} = {builder.ParameterPrefix}replayable;");
        builder.AddNamedParameter("replayable", true);

        // This operation writes two statements, so the boundary has to be explicit -- the trailing
        // semicolon is enough for the providers that concatenate, but Oracle needs a real split
        builder.StartNewCommand();

        builder.Append(
            $"delete from {_database.TableNameFor(DatabaseConstants.DeadLetterTable)} where {DatabaseConstants.Replayable} = {builder.ParameterPrefix}replayable;");
    }

    public Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}