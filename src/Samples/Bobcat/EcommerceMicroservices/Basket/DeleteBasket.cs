using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Basket;

public static class DeleteBasketEndpoint
{
    [WolverineDelete("/basket/{userName}")]
    public static void Delete(string userName, [Entity(Required = true)] ShoppingCart cart, IDocumentSession session)
    {
        session.Delete(cart);
    }
}
