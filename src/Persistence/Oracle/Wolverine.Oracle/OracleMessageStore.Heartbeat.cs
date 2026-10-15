using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore : IMessageInboxHeartbeat
{
    // Oracle caps an IN list at 1,000 expressions
    private const int HeartbeatChunkSize = 500;

    /// <summary>
    /// Restart the <see cref="DurabilitySettings.InboxStaleTime" /> clock of rows this node is still holding in
    /// memory. Only rows still waiting to run and still owned by <paramref name="ownerId" /> are touched. A no-op
    /// unless InboxStaleTime is set, since the column only exists then.
    /// </summary>
    public async Task RestartStaleClockAsync(int ownerId, IReadOnlyList<Envelope> envelopes)
    {
        if (!_durability.InboxStaleTime.HasValue || envelopes.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        try
        {
            foreach (var byAddress in envelopes.GroupBy(x => x.Destination!.ToString()))
            {
                foreach (var chunk in byAddress.Chunk(HeartbeatChunkSize))
                {
                    var ids = string.Join(", ", chunk.Select((_, i) => $":id{i}"));
                    await using var cmd = conn.CreateCommand(
                        $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.Timestamp} = SYSTIMESTAMP AT TIME ZONE 'UTC' " +
                        $"WHERE {DatabaseConstants.OwnerId} = :owner AND {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' " +
                        $"AND {DatabaseConstants.ReceivedAt} = :uri AND id IN ({ids})");
                    cmd.With("owner", ownerId);
                    cmd.With("uri", byAddress.Key);
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        cmd.With($"id{i}", chunk[i].Id);
                    }

                    await cmd.ExecuteNonQueryAsync(_cancellation);
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
