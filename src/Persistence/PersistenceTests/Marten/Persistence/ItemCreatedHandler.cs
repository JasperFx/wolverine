using Marten;
using Wolverine.Attributes;

namespace Wolverine.Persistence.Testing.Marten.Persistence;

public class ItemCreatedHandler
{
    [Transactional]
    public static void Handle(ItemCreated created, IDocumentSession session,
        Envelope envelope)
    {
        session.Store(created);
    }
}
