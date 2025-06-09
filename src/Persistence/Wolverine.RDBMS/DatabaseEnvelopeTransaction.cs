﻿using System.Data.Common;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS;

public class DatabaseEnvelopeTransaction : IEnvelopeTransaction, IDisposable
{
    private readonly IMessageDatabase _database;
    private readonly DbTransaction _tx;

    public DatabaseEnvelopeTransaction(IMessageDatabase database, DbTransaction tx)
    {
        _tx = tx;
        _database = database;
    }

    public void Dispose()
    {
        _tx.Dispose();
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        return PersistOutgoingAsync([envelope]);
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (envelopes.Length == 0)
        {
            return Task.CompletedTask;
        }

        return _database.StoreOutgoingAsync(_tx, envelopes);
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        return _database.StoreIncomingAsync(_tx, [envelope]);
    }

    public ValueTask RollbackAsync()
    {
        return new ValueTask(_tx.RollbackAsync());
    }
}