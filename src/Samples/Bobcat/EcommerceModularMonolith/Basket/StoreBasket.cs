using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;

namespace Basket;

public record StoreBasket(ShoppingCart Cart);

public static class StoreBasketEndpoint
{
    // An upsert keyed by user name, answered with 201 and the basket's own URL — the same
    // contract the original eShop sample had. See docs/sample-wiring.md footgun 3 for why this
    // is a Created<T> and not a (ShoppingCart, IResult) tuple.
    [WolverinePost("/basket")]
    public static Created<ShoppingCart> Post(StoreBasket command, IDocumentSession session)
    {
        session.Store(command.Cart);
        return TypedResults.Created($"/basket/{command.Cart.Id}", command.Cart);
    }
}
