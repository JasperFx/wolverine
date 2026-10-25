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

        // This operation writes several statements, so the boundaries have to be explicit -- the trailing
        // semicolon is enough for the providers that concatenate, but Oracle needs a real split
        builder.StartNewCommand();

        // GH-5209. The delete below loses the dead letter row, so record its replay history first. The dead letter
        // row already carries any earlier history (the dead letter insert read it), so the new history row simply
        // replaces the old one: delete-then-insert rather than a provider specific upsert. Grouped by id because
        // MessageIdentity.IdAndDestination allows one id per destination.
        var history = _database.TableNameFor(DatabaseConstants.DeadLetterHistoryTable);
        var deadLetters = _database.TableNameFor(DatabaseConstants.DeadLetterTable);
        builder.Append(
            $"delete from {history} where {DatabaseConstants.Id} in (select {DatabaseConstants.Id} from {deadLetters} where {DatabaseConstants.Replayable} = {builder.ParameterPrefix}replayable);");

        builder.StartNewCommand();

        builder.Append(
            $"insert into {history} ({DatabaseConstants.Id}, {DatabaseConstants.ReplayCount}, {DatabaseConstants.LastReplayedAt}, {DatabaseConstants.FirstFailedAt}) " +
            $"select {DatabaseConstants.Id}, max(coalesce({DatabaseConstants.ReplayCount}, 0)) + 1, {builder.ParameterPrefix}replayed_at, " +
            $"min(coalesce({DatabaseConstants.FirstFailedAt}, {DatabaseConstants.FailedAt}, {DatabaseConstants.SentAt})) " +
            $"from {deadLetters} where {DatabaseConstants.Replayable} = {builder.ParameterPrefix}replayable group by {DatabaseConstants.Id};");
        builder.AddNamedParameter("replayed_at", DateTimeOffset.UtcNow);

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