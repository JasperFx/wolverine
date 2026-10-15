using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : IMessageInboxHeartbeat
{
    // Well under SQL Server's 2,100-parameter ceiling, the tightest of the supported databases
    private const int HeartbeatChunkSize = 500;

    /// <summary>
    /// Restart the <see cref="DurabilitySettings.InboxStaleTime" /> clock of rows this node is still holding in
    /// memory, on the same clock as the column's default. Only rows still waiting to run and still owned by
    /// <paramref name="ownerId" /> are touched. A no-op unless InboxStaleTime is set, since the column only
    /// exists then.
    /// </summary>
    public Task RestartStaleClockAsync(int ownerId, IReadOnlyList<Envelope> envelopes)
    {
        if (!Durability.InboxStaleTime.HasValue || envelopes.Count == 0) return Task.CompletedTask;

        var builder = ToCommandBuilder();

        // One statement per receiving address, chunked, so the id list stays well inside any parameter limit
        foreach (var byAddress in envelopes.GroupBy(x => x.Destination!.ToString()))
        {
            foreach (var chunk in byAddress.Chunk(HeartbeatChunkSize))
            {
                builder.Append(
                    $"update {QuotedTableNameFor(DatabaseConstants.IncomingTable)} set {DatabaseConstants.Timestamp} = {currentTimestampSql} where {DatabaseConstants.OwnerId} = ");
                builder.AppendParameter(ownerId);
                builder.Append($" and {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = ");
                builder.AppendParameter(byAddress.Key);
                builder.Append($" and {DatabaseConstants.Id} in (");
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (i > 0) builder.Append(", ");
                    builder.AppendParameter(chunk[i].Id);
                }

                builder.Append(");");
            }
        }

        return executeCommandBatch(builder, _cancellation);
    }
}
