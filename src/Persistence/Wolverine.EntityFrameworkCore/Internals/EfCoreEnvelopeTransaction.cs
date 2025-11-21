using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Weasel.Core;
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

    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (envelope.WasPersistedInInbox) return true;
        
        if (DbContext.Database.CurrentTransaction == null)
        {
            await DbContext.Database.BeginTransactionAsync(cancellation);
        }

        try
        {
            var copy = Envelope.ForPersistedHandled(envelope);
            await PersistIncomingAsync(copy);
            
            // Gotta flush the call to the database!
            if (DbContext.IsWolverineEnabled())
            {
                await DbContext.SaveChangesAsync(cancellation);
            }
            
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch (Exception )
        {
            if (DbContext.Database.CurrentTransaction != null)
            {
                await DbContext.Database.CurrentTransaction.RollbackAsync(cancellation);
            }
            
            return false;
        }
    }

    public async ValueTask CommitAsync(CancellationToken cancellation)
    {
        if (_messaging.Envelope != null && _messaging.Envelope.Destination != null)
        {
            var conn = DbContext.Database.GetDbConnection();
            var tx = DbContext.Database.CurrentTransaction!.GetDbTransaction();
            
            // Are we marking an existing envelope as persisted?
            if (_messaging.Envelope.WasPersistedInInbox)
            { 
                var cmd = conn.CreateCommand(
                        $"update {_database.SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' where id = @id")
                    .With("id", _messaging.Envelope.Id);
                cmd.Transaction = tx;
                await cmd.ExecuteNonQueryAsync(cancellation);
            }
            
            // Or inserting a record just to tell the inbox about
            // handled messages for the sake of idempotency
            else
            {
                var envelope = Envelope.ForPersistedHandled(_messaging.Envelope);
                await PersistIncomingAsync(envelope);
            }
            
            _messaging.Envelope.Status = EnvelopeStatus.Handled;
        }

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