using Wolverine.Http;
using Wolverine.Persistence;

namespace Basket;

public static class GetBasketEndpoint
{
    [WolverineGet("/basket/{userName}")]
    public static ShoppingCart? Get(string userName, [Entity] ShoppingCart? cart) => cart;
}
