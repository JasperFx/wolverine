using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// GH-5209. The statements that expire dead letters: unarchived dead letters past their <c>expires</c> time when
/// <see cref="DurabilitySettings.DeadLetterQueueExpirationEnabled"/> is set (unchanged), archived dead letters
/// once <see cref="DurabilitySettings.ArchivedDeadLetterRetention"/> has passed since they were archived, and
/// replay history that has outlived the dead letter expiration window
/// </summary>
internal static class DeadLetterExpiration
{
    public static void WriteStatements(DbCommandBuilder builder, IMessageDatabase database,
        DurabilitySettings? settings, DateTimeOffset now)
    {
        var deadLetters = database.TableNameFor(DatabaseConstants.DeadLetterTable);

        // Without settings this is the original expiration statement and nothing else
        if (settings == null || settings.DeadLetterQueueExpirationEnabled)
        {
            builder.Append($"delete from {deadLetters} where {DatabaseConstants.Expires} < ");
            builder.AppendParameter(now);

            // When archived dead letters have their own retention, the expires column no longer governs them
            if (settings?.ArchivedDeadLetterRetention != null)
            {
                builder.Append($" and {DatabaseConstants.ArchivedAt} is null");
            }

            builder.Append(';');
        }

        if (settings == null) return;

        if (settings.ArchivedDeadLetterRetention.HasValue)
        {
            builder.StartNewCommand();
            builder.Append($"delete from {deadLetters} where {DatabaseConstants.ArchivedAt} < ");
            builder.AppendParameter(now.Subtract(settings.ArchivedDeadLetterRetention.Value));
            builder.Append(';');
        }

        // A dead letter that fails again carries its history on its own row, so history older than the dead
        // letter expiration window only describes messages that were replayed and never failed again
        var historyWindow = settings.DeadLetterQueueExpiration;
        if (settings.ArchivedDeadLetterRetention > historyWindow)
        {
            historyWindow = settings.ArchivedDeadLetterRetention.Value;
        }

        builder.StartNewCommand();
        builder.Append(
            $"delete from {database.TableNameFor(DatabaseConstants.DeadLetterHistoryTable)} where {DatabaseConstants.LastReplayedAt} < ");
        builder.AppendParameter(now.Subtract(historyWindow));
        builder.Append(';');
    }
}
