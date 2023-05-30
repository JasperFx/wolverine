using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Util;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public abstract Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);
    public abstract Task DeleteOutgoingAsync(Envelope[] envelopes);

    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        var envelopes = await conn.CreateCommand(_outgoingEnvelopeSql)
            .With("destination", destination.ToString())
            .FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);

        await conn.CloseAsync();

        return envelopes;
    }

    public abstract Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing);

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        return CreateCommand(
                $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
            .With("id", envelope.Id)
            .ExecuteOnce(_cancellation);
    }

    [Obsolete("Goes away")]
    public async Task<Uri[]> FindAllDestinationsAsync()
    {
        var cmd = Session.CreateCommand(
            $"select distinct destination from {SchemaName}.{DatabaseConstants.OutgoingTable}");
        var uris = await cmd.FetchListAsync<string>(_cancellation);
        return uris.Where(x => x != null).Select(x => x!.ToUri()).ToArray();
    }

    public Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return DatabasePersistence.BuildOutgoingStorageCommand(envelope, ownerId, this)
            .ExecuteOnce(_cancellation);
    }

    public Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelopes, Durability.AssignedNodeNumber, this);
        cmd.Connection = tx.Connection;
        cmd.Transaction = tx;

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    protected abstract string
        determineOutgoingEnvelopeSql(DurabilitySettings settings);
}