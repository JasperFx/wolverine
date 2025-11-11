using System.Reflection;
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
public class EfCoreEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly MessageContext _messaging;
    private readonly IMessageDatabase _database;
    
    public EfCoreEnvelopeTransaction(DbContext dbContext, MessageContext messaging)
    {
        _messaging = messaging;
        if (!messaging.TryFindMessageDatabase(out _database))
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using Database backed message persistence. Please configure the message persistence");
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

        if (DbContext.IsWolverineEnabled())
        {
            DbContext.Add(new OutgoingMessage(envelope));
        }
        else
        {
            var conn = DbContext.Database.GetDbConnection();
            var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
            var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelope, envelope.OwnerId, _database);
            cmd.Transaction = tx;
            cmd.Connection = conn;

            await cmd.ExecuteNonQueryAsync();
        }


    }

    public async Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (envelopes.Length == 0)
        {
            return;
        }

        if (DbContext.Database.CurrentTransaction == null)
        {
            await DbContext.Database.BeginTransactionAsync();
        }

        if (DbContext.IsWolverineEnabled())
        {
            foreach (var envelope in envelopes)
            {
                var outgoing = new OutgoingMessage(envelope);
                DbContext.Add(outgoing);
            }
        }
        else
        {
            var conn = DbContext.Database.GetDbConnection();
            var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
            var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, _database);
            cmd.Transaction = tx;
            cmd.Connection = conn;

            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task PersistIncomingAsync(Envelope envelope)
    {
        if (DbContext.Database.CurrentTransaction == null)
        {
            await DbContext.Database.BeginTransactionAsync();
        }

        if (DbContext.IsWolverineEnabled())
        {
            DbContext.Add(new IncomingMessage(envelope));
        }
        else
        {
            var conn = DbContext.Database.GetDbConnection();
            var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
            var builder = _database.ToCommandBuilder();
            DatabasePersistence.BuildIncomingStorageCommand(_database, builder, envelope);


            var command = builder.Compile();
            command.Connection = conn;
            command.Transaction = tx;
            await command.ExecuteNonQueryAsync();
        }
    }

    public ValueTask RollbackAsync()
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            return new ValueTask(DbContext.Database.CurrentTransaction.RollbackAsync());
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CommitAsync(CancellationToken cancellation)
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            await DbContext.Database.CurrentTransaction.CommitAsync(cancellation);
        }

        await _messaging.FlushOutgoingMessagesAsync();
    }
    
    public static bool IsDisposed(DbContext context)
    {
        var result = true;

        var typeDbContext = typeof(DbContext);
        var typeInternalContext = typeDbContext.Assembly.GetType("System.Data.Entity.Internal.InternalContext");

        var internalContextField = typeDbContext.GetField("_internalContext", BindingFlags.NonPublic | BindingFlags.Instance);
        var isDisposedProperty = typeInternalContext!.GetProperty("IsDisposed");

        var ic = internalContextField!.GetValue(context);

        if (ic != null)
        {
            result = (bool)isDisposedProperty!.GetValue(ic)!;
        }

        return result;
    }
}