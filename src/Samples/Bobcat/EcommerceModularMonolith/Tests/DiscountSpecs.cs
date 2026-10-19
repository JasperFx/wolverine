namespace EcommerceModularMonolith.Tests;

[BobcatFeature("Ecommerce Modular Monolith: discount")]
public class DiscountSpecs(EcommerceModularMonolithHost app) : EcommerceModularMonolithSpec(app)
{
    [BobcatSpec]
    public async Task create_a_discount()
    {
        await WhenADiscountIsCreated("DiscountedProduct", 10);

        ThenTheResponseStatusIs(201);
    }

    [BobcatSpec]
    public async Task get_discount_by_product_name()
    {
        await GivenADiscount("SearchedProduct", 15);

        await WhenTheDiscountIsRead("SearchedProduct");

        ThenTheResponseStatusIs(200);
        ThenTheDiscountAmountIs(15);
    }

    [BobcatSpec]
    public async Task update_a_discount()
    {
        await GivenADiscount("UpdatableProduct", 5);

        await WhenTheDiscountIsChanged(20);

        ThenTheResponseStatusIs(200);
        await ThenTheStoredDiscountIs("UpdatableProduct", 20);
    }

    [BobcatSpec]
    public async Task delete_a_discount()
    {
        await GivenADiscount("DeletableDiscount", 10);

        await WhenTheDiscountIsDeleted();

        ThenTheResponseStatusIs(204);
    }
}
