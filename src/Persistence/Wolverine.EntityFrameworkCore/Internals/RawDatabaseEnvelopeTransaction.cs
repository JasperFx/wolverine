using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

// ReSharper disable once InconsistentNaming
/// <summary>
///     Envelope transaction for raw database access for DbContexts w/o the explicit wolverine mappings
/// </summary>
public class RawDatabaseEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly IMessageDatabase _database;

    public RawDatabaseEnvelopeTransaction(DbContext dbContext, MessageContext messaging)
    {
        if (messaging.Storage is IMessageDatabase persistence)
        {
            _database = persistence;
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
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelope, envelope.OwnerId, _database);
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
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, _database);
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
        var builder = _database.ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(_database, builder, envelope);
        await builder.ExecuteNonQueryAsync(conn, tx: tx);
    }

    public ValueTask RollbackAsync()
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            return new ValueTask(DbContext.Database.CurrentTransaction.RollbackAsync());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CommitAsync()
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            return new ValueTask(DbContext.Database.CurrentTransaction.CommitAsync());
        }

        return ValueTask.CompletedTask;
    }
}