using Weasel.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    /// <summary>
    /// GH-5049. Whether a retained Handled row exists for the envelope's identity, by the same identity rule as
    /// <see cref="ExistsAsync" />
    /// </summary>
    public async Task<bool> IsHandledAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (HasDisposed) return false;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellation);

        var sql = _durability.MessageIdentity == MessageIdentity.IdOnly
            ? $"SELECT COUNT(id) FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = :id AND {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'"
            : $"SELECT COUNT(id) FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = :id AND {DatabaseConstants.ReceivedAt} = :destination AND {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'";

        var cmd = conn.CreateCommand(sql);
        cmd.With("id", envelope.Id);
        if (_durability.MessageIdentity != MessageIdentity.IdOnly)
        {
            cmd.With("destination", envelope.Destination!.ToString());
        }

        var count = await cmd.ExecuteScalarAsync(cancellation);
        await conn.CloseAsync();

        return Convert.ToInt64(count) > 0;
    }
}
