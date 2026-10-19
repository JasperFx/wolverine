using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Ordering;

public static class DeleteOrderEndpoint
{
    [WolverineDelete("/orders/{id}")]
    public static void Delete(Guid id, [Entity(Required = true)] Order order, IDocumentSession session)
    {
        session.Delete(order);
    }
}
