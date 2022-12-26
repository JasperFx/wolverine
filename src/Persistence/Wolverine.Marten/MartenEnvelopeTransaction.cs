﻿using Marten;
using Wolverine.Marten.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace Wolverine.Marten;

internal class MartenEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly int _nodeId;
    private readonly PostgresqlSettings _settings;

    public MartenEnvelopeTransaction(IDocumentSession session, MessageContext context)
    {
        if (context.Storage is PostgresqlMessageStore persistence)
        {
            _settings = (PostgresqlSettings)persistence.DatabaseSettings;
            _nodeId = persistence.Settings.UniqueNodeId;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using Postgresql + Marten as the backing message persistence");
        }

        Session = session;
    }

    public IDocumentSession Session { get; }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        Session.StoreOutgoing(_settings, envelope, _nodeId);
        return Task.CompletedTask;
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes) Session.StoreOutgoing(_settings, envelope, _nodeId);

        return Task.CompletedTask;
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        Session.StoreIncoming(_settings, envelope);
        return Task.CompletedTask;
    }

    public Task CopyToAsync(IEnvelopeTransaction other)
    {
        throw new NotSupportedException();
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
    }
}