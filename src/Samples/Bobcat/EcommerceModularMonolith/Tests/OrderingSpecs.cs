namespace EcommerceModularMonolith.Tests;

/// <summary>
/// A checkout in the Basket module becomes an order in the Ordering module, carried over a durable local
/// queue -- so every spec here arranges its order through a checkout rather than storing one directly.
/// </summary>
[BobcatFeature("Ecommerce Modular Monolith: ordering")]
public class OrderingSpecs(EcommerceModularMonolithHost app) : EcommerceModularMonolithSpec(app)
{
    [BobcatSpec]
    public async Task create_an_order()
    {
        await GivenACatalogProduct("OrderItem", 15.00m);
        await GivenABasket("orderCustomer");
        await GivenTheBasketWasCheckedOut("orderCustomer");

        await WhenAllOrdersAreRead();

        ThenAtLeastOrdersAreReturned(1);
        ThenAnOrderExistsForTheCheckedOutCustomer();
    }

    [BobcatSpec]
    public async Task get_all_orders()
    {
        await WhenAllOrdersAreRead();

        ThenTheResponseStatusIs(200);
    }

    [BobcatSpec]
    public async Task get_order_by_id()
    {
        await GivenACatalogProduct("OrderByIdItem", 15.00m);
        await GivenABasket("orderByIdCustomer");
        await GivenTheBasketWasCheckedOut("orderByIdCustomer");

        await WhenTheCheckoutOrderIsRead();

        ThenTheResponseStatusIs(200);
    }

    [BobcatSpec]
    public async Task delete_an_order()
    {
        await GivenACatalogProduct("DeleteOrderItem", 15.00m);
        await GivenABasket("deleteOrderCustomer");
        await GivenTheBasketWasCheckedOut("deleteOrderCustomer");

        await WhenTheCheckoutOrderIsDeleted();

        ThenTheResponseStatusIs(204);
    }
}
