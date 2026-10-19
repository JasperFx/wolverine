using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Basket;

public static class DeleteBasketEndpoint
{
    [WolverineDelete("/basket/{userName}")]
    public static void Delete([Entity("userName", Required = true)] ShoppingCart cart, IDocumentSession session)
    {
        session.Delete(cart);
    }
}
