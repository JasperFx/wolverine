using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

// ReSharper disable once InconsistentNaming
internal class EfCoreEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly DatabaseSettings _settings;

    public EfCoreEnvelopeTransaction(DbContext dbContext, MessageContext messaging)
    {
        if (messaging.Storage is IMessageDatabase persistence)
        {
            _settings = persistence.Settings;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using Database backed message persistence. Please configure the message configuration");
        }

        DbContext = dbContext;
    }

    public DbContext DbContext { get; }

    public async Task PersistOutgoingAsync(Envelope envelope)
    {
        if (DbContext.Database.CurrentTransaction == null)
        {
            await DbContext.Database.BeginTransactionAsync();
        }

        var conn = DbContext.Database.GetDbConnection();
        var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelope, envelope.OwnerId, _settings);
        cmd.Transaction = tx;
        cmd.Connection = conn;

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (!envelopes.Any())
        {
            return;
        }

        if (DbContext.Database.CurrentTransaction == null)
        {
            await DbContext.Database.BeginTransactionAsync();
        }

        var conn = DbContext.Database.GetDbConnection();
        var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, _settings);
        cmd.Transaction = tx;
        cmd.Connection = conn;

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PersistIncomingAsync(Envelope envelope)
    {
        if (DbContext.Database.CurrentTransaction == null)
        {
            await DbContext.Database.BeginTransactionAsync();
        }

        var conn = DbContext.Database.GetDbConnection();
        var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
        var builder = _settings.ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(_settings, builder, envelope);
        await builder.ExecuteNonQueryAsync(conn, tx: tx);
    }

    public Task CopyToAsync(IEnvelopeTransaction other)
    {
        throw new NotSupportedException();
    }

    public ValueTask RollbackAsync()
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            return new ValueTask(DbContext.Database.CurrentTransaction.RollbackAsync());
        }

        return ValueTask.CompletedTask;
    }
}