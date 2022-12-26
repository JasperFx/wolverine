﻿using System.Data.Common;
using Weasel.Core;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.RDBMS;

public abstract partial class DatabaseBackedMessageStore<T>
{
    public abstract Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);
    public abstract Task DeleteOutgoingAsync(Envelope[] envelopes);

    public Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        if (Session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }

        return Session.Transaction.CreateCommand(_outgoingEnvelopeSql)
            .With("destination", destination.ToString())
            .FetchList(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);
    }

    public abstract Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing);

    public Task DeleteByDestinationAsync(Uri? destination)
    {
        if (Session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }

        return Session.Transaction
            .CreateCommand(
                $"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = :owner and destination = @destination")
            .With("destination", destination!.ToString())
            .With("owner", TransportConstants.AnyNode)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        return DatabaseSettings
            .CreateCommand(
                $"delete from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
            .With("id", envelope.Id)
            .ExecuteOnce(_cancellation);
    }

    public async Task<Uri[]> FindAllDestinationsAsync()
    {
        var cmd = Session.CreateCommand(
            $"select distinct destination from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}");
        var uris = await cmd.FetchList<string>(_cancellation);
        return uris.Where(x => x != null).Select(x => x!.ToUri()).ToArray();
    }

    public Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return DatabasePersistence.BuildOutgoingStorageCommand(envelope, ownerId, DatabaseSettings)
            .ExecuteOnce(_cancellation);
    }


    public Task StoreOutgoingAsync(Envelope[] envelopes, int ownerId)
    {
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelopes, ownerId, DatabaseSettings);
        return cmd.ExecuteOnce(CancellationToken.None);
    }

    public Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelopes, Settings.UniqueNodeId, DatabaseSettings);
        cmd.Connection = tx.Connection;
        cmd.Transaction = tx;

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    protected abstract string
        determineOutgoingEnvelopeSql(DatabaseSettings databaseSettings, AdvancedSettings settings);
}