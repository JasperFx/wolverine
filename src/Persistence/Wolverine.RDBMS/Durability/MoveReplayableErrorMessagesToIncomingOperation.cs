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

        // GH-5395. At READ COMMITTED (the PostgreSQL, SQL Server and Oracle default) each statement below takes a
        // snapshot of its own, so "replayable = true" alone can match a dead letter marked after the insert above
        // already ran -- and the delete would then drop a row that never reached the inbox. Everything from here on is limited to the dead letters that
        // insert copied, found by their uncommitted inbox row, which no other transaction can add or remove.
        var incoming = _database.TableNameFor(DatabaseConstants.IncomingTable);
        var deadLetters = _database.TableNameFor(DatabaseConstants.DeadLetterTable);
        var copied =
            $"{DatabaseConstants.Replayable} = {builder.ParameterPrefix}replayable and exists (select 1 from {incoming} i " +
            $"where i.{DatabaseConstants.Id} = {deadLetters}.{DatabaseConstants.Id} " +
            $"and (i.{DatabaseConstants.ReceivedAt} = {deadLetters}.{DatabaseConstants.ReceivedAt} " +
            $"or (i.{DatabaseConstants.ReceivedAt} is null and {deadLetters}.{DatabaseConstants.ReceivedAt} is null)))";

        // GH-5209. The delete below loses the dead letter row, so record its replay history first. The dead letter
        // row already carries any earlier history (the dead letter insert read it), so the new history row simply
        // replaces the old one: delete-then-insert rather than a provider specific upsert. Grouped by id because
        // MessageIdentity.IdAndDestination allows one id per destination.
        var history = _database.TableNameFor(DatabaseConstants.DeadLetterHistoryTable);
        builder.Append(
            $"delete from {history} where {DatabaseConstants.Id} in (select {DatabaseConstants.Id} from {deadLetters} where {copied});");

        builder.StartNewCommand();

        builder.Append(
            $"insert into {history} ({DatabaseConstants.Id}, {DatabaseConstants.ReplayCount}, {DatabaseConstants.LastReplayedAt}, {DatabaseConstants.FirstFailedAt}) " +
            $"select {DatabaseConstants.Id}, max(coalesce({DatabaseConstants.ReplayCount}, 0)) + 1, {builder.ParameterPrefix}replayed_at, " +
            $"min(coalesce({DatabaseConstants.FirstFailedAt}, {DatabaseConstants.FailedAt}, {DatabaseConstants.SentAt})) " +
            $"from {deadLetters} where {copied} group by {DatabaseConstants.Id};");
        builder.AddNamedParameter("replayed_at", DateTimeOffset.UtcNow);

        builder.StartNewCommand();

        builder.Append($"delete from {deadLetters} where {copied};");
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