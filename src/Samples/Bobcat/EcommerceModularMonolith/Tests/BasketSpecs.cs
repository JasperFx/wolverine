namespace EcommerceModularMonolith.Tests;

[BobcatFeature("Ecommerce Modular Monolith: basket")]
public class BasketSpecs(EcommerceModularMonolithHost app) : EcommerceModularMonolithSpec(app)
{
    [BobcatSpec]
    public async Task store_basket()
    {
        await GivenACatalogProduct("BasketItem", 10.00m);

        await WhenABasketIsStored("customer1");

        ThenTheResponseStatusIs(201);
    }

    [BobcatSpec]
    public async Task get_basket()
    {
        await GivenACatalogProduct("GetBasketItem", 10.00m);
        await GivenABasket("getBasketCustomer");

        await WhenTheBasketIsRead("getBasketCustomer");

        ThenTheResponseStatusIs(200);
        ThenTheBasketTotalIs(10.00m);
    }

    [BobcatSpec]
    public async Task delete_basket()
    {
        await GivenACatalogProduct("DelBasketItem", 10.00m);
        await GivenABasket("delBasketCustomer");

        await WhenTheBasketIsDeleted("delBasketCustomer");

        ThenTheResponseStatusIs(204);
        await ThenTheBasketIsGone("delBasketCustomer");
    }

    [BobcatSpec]
    public async Task checkout_basket()
    {
        await GivenACatalogProduct("CheckoutItem", 25.00m);
        await GivenABasket("checkoutCustomer");

        await WhenTheBasketIsCheckedOut("checkoutCustomer");

        ThenTheResponseStatusIs(202);
        await ThenTheBasketIsGone("checkoutCustomer");
    }

    [BobcatSpec]
    public async Task checkout_a_basket_that_does_not_exist()
    {
        await WhenTheBasketIsCheckedOut("nobody");

        ThenTheResponseStatusIs(404);
    }
}
