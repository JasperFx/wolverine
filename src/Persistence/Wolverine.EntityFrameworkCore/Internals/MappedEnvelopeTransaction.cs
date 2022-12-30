using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// This envelope transaction should be used for DbContext types where the Wolverine
/// mappings have been applied to the DbContext
/// </summary>
internal class MappedEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly DatabaseSettings _settings;
    
    public MappedEnvelopeTransaction(DbContext dbContext, MessageContext messaging)
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