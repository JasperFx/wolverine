using System;
using System.Linq;
using System.Threading.Tasks;
using Wolverine.RDBMS;
using Wolverine.Persistence.Durability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Wolverine.EntityFrameworkCore;

// ReSharper disable once InconsistentNaming
public class EfCoreEnvelopeOutbox : IEnvelopeOutbox
{
    private readonly DbContext _db;
    private readonly DatabaseSettings _settings;

    public EfCoreEnvelopeOutbox(DbContext db, IMessageContext messaging)
    {
        if (messaging.Persistence is IDatabaseBackedEnvelopePersistence persistence)
        {
            _settings = persistence.DatabaseSettings;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using Database backed message persistence. Please configure the message configuration");
        }

        _db = db;
    }

    public async Task PersistAsync(Envelope envelope)
    {
        if (_db.Database.CurrentTransaction == null)
        {
            await _db.Database.BeginTransactionAsync();
        }

        var conn = _db.Database.GetDbConnection();
        var tx = _db.Database.CurrentTransaction!.GetDbTransaction();
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelope, envelope.OwnerId, _settings);
        cmd.Transaction = tx;
        cmd.Connection = conn;

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PersistAsync(Envelope[] envelopes)
    {
        if (!envelopes.Any())
        {
            return;
        }

        if (_db.Database.CurrentTransaction == null)
        {
            await _db.Database.BeginTransactionAsync();
        }

        var conn = _db.Database.GetDbConnection();
        var tx = _db.Database.CurrentTransaction!.GetDbTransaction();
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, _settings);
        cmd.Transaction = tx;
        cmd.Connection = conn;

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ScheduleJobAsync(Envelope envelope)
    {
        if (_db.Database.CurrentTransaction == null)
        {
            await _db.Database.BeginTransactionAsync();
        }

        var conn = _db.Database.GetDbConnection();
        var tx = _db.Database.CurrentTransaction!.GetDbTransaction();
        var builder = _settings.ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(_settings, builder, envelope);
        await builder.ExecuteNonQueryAsync(conn, tx: tx);
    }

    public Task CopyToAsync(IEnvelopeOutbox other)
    {
        throw new NotSupportedException();
    }

    public async ValueTask RollbackAsync()
    {
        if (_db.Database.CurrentTransaction != null)
        {
            await _db.Database.CurrentTransaction.RollbackAsync();
        }
    }
}
