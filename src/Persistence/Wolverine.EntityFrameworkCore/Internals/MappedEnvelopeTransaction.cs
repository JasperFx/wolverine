using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     This envelope transaction should be used for DbContext types where the Wolverine
///     mappings have been applied to the DbContext
/// </summary>
internal class MappedEnvelopeTransaction : IEnvelopeTransaction
{
    public MappedEnvelopeTransaction(DbContext dbContext, MessageContext messaging)
    {
        DbContext = dbContext;
    }

    public DbContext DbContext { get; }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        DbContext.Add(new OutgoingMessage(envelope));
        return Task.CompletedTask;
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            var outgoing = new OutgoingMessage(envelope);
            DbContext.Add(outgoing);
        }

        return Task.CompletedTask;
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        DbContext.Add(new IncomingMessage(envelope));

        return Task.CompletedTask;
    }

    public ValueTask RollbackAsync()
    {
        if (DbContext.Database.CurrentTransaction != null)
        {
            return new ValueTask(DbContext.Database.CurrentTransaction.RollbackAsync());
        }

        return ValueTask.CompletedTask;
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