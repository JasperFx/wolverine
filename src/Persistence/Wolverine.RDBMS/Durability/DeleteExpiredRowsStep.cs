using Microsoft.Extensions.Logging;
using Weasel.Core;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

/// <summary>
///     GH-5028. The expiry cleanups of a <see cref="DurabilityMode.ServerlessWithOutbox" /> durability pass, which
///     has no <c>DurabilityAgent</c> timers to run them: one batch of expired handled envelopes per call, then the
///     expired dead letters (when <see cref="DurabilitySettings.DeadLetterQueueExpirationEnabled" />) and the
///     expired deduplication claims (when deduplication is on)
/// </summary>
public sealed class DeleteExpiredRowsStep : IDurabilityPassStep
{
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;
    private bool _othersDone;

    public DeleteExpiredRowsStep(IMessageDatabase database, ILogger logger)
    {
        _database = database;
        _logger = logger;
    }

    public string Name => "Delete expired rows";

    public async Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        if (_database.HasDisposed) return DurabilityPassStepResult.Nothing;

        var now = context.Now;
        var settings = context.Runtime.Options.Durability;

        var handled = await deleteExpiredHandledAsync(now, context.BatchSize, token);
        var deleted = handled;

        // Neither of these is batched, so they only need to run once per pass
        if (!_othersDone)
        {
            _othersDone = true;

            if (settings.DeadLetterQueueExpirationEnabled)
            {
                deleted += await deleteExpiredDeadLettersAsync(now, token);
            }

            if (settings.MessageDeduplicationMode != MessageDeduplicationMode.None)
            {
                deleted += await _database.Deduplication.DeleteExpiredAsync(now, token);
            }
        }

        var remaining = handled < context.BatchSize ? 0 : await countExpiredHandledAsync(now, token);

        if (deleted > 0)
        {
            _logger.LogInformation("Durability pass deleted {Count} expired row(s) from {Store}", deleted,
                _database.Uri);
        }

        return new DurabilityPassStepResult { ExpiredDeleted = deleted, Remaining = remaining };
    }

    private async Task<int> deleteExpiredHandledAsync(DateTimeOffset now, int batchSize, CancellationToken token)
    {
        var sql = _database.BatchedDeleteExpiredHandledEnvelopesSql(batchSize);
        if (string.IsNullOrEmpty(sql))
        {
            // A provider with no bounded delete takes them all at once
            var builder = _database.ToCommandBuilder();
            builder.Append(
                $"delete from {_database.TableNameFor(DatabaseConstants.IncomingTable)} where {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' and {DatabaseConstants.KeepUntil} <= ");
            builder.AppendParameter(now);
            return await executeNonQueryAsync(builder, token);
        }

        await using var conn = await _database.DataSource.OpenConnectionAsync(token);
        return await conn.CreateCommand(sql).With("now", now).ExecuteNonQueryAsync(token);
    }

    private Task<int> deleteExpiredDeadLettersAsync(DateTimeOffset now, CancellationToken token)
    {
        var builder = _database.ToCommandBuilder();
        builder.Append(
            $"delete from {_database.TableNameFor(DatabaseConstants.DeadLetterTable)} where {DatabaseConstants.Expires} < ");
        builder.AppendParameter(now);
        return executeNonQueryAsync(builder, token);
    }

    private async Task<int> countExpiredHandledAsync(DateTimeOffset now, CancellationToken token)
    {
        var builder = _database.ToCommandBuilder();
        builder.Append(
            $"select count(*) from {_database.TableNameFor(DatabaseConstants.IncomingTable)} where {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' and {DatabaseConstants.KeepUntil} <= ");
        builder.AppendParameter(now);

        await using var conn = await _database.DataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private async Task<int> executeNonQueryAsync(DbCommandBuilder builder, CancellationToken token)
    {
        await using var conn = await _database.DataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;
        return await cmd.ExecuteNonQueryAsync(token);
    }
}
