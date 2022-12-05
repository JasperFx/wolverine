using Marten;
using Wolverine.Postgresql;

namespace Wolverine.Marten.Persistence.Operations;

internal static class MartenStorageExtensions
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