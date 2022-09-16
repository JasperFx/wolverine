using Wolverine.Persistence.Postgresql;
using Marten;

namespace Wolverine.Persistence.Marten.Persistence.Operations;

public static class MartenStorageExtensions
{
    public static void StoreIncoming(this IDocumentSession session, PostgresqlSettings settings, Envelope envelope)
    {
        var operation = new StoreIncomingEnvelope(settings.IncomingFullName, envelope);
        session.QueueOperation(operation);
    }

    public static void StoreOutgoing(this IDocumentSession session, PostgresqlSettings settings, Envelope envelope,
        int ownerId)
    {
        var operation = new StoreOutgoingEnvelope(settings.OutgoingFullName, envelope, ownerId);
        session.QueueOperation(operation);
    }
}
