using Marten;
using Wolverine;
using Wolverine.Attributes;

namespace PersistenceTests.Marten.Persistence;

public class ItemCreatedHandler
{
    [Transactional]
    public static void Handle(ItemCreated created, IDocumentSession session,
        Envelope envelope)
    {
        session.Store(created);
    }
}