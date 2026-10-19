using Marten;
using Wolverine.Http;

namespace Basket;

public record StoreBasket(ShoppingCart Cart);

public static class StoreBasketEndpoint
{
    [WolverinePost("/basket")]
    public static ShoppingCart Post(StoreBasket command, IDocumentSession session)
    {
        session.Store(command.Cart);
        return command.Cart;
    }
}
