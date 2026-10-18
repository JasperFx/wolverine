namespace EcommerceModularMonolith.Tests;

[BobcatFeature("Ecommerce Modular Monolith: catalog")]
public class CatalogSpecs(EcommerceModularMonolithHost app) : EcommerceModularMonolithSpec(app)
{
    [BobcatSpec]
    public async Task create_catalog_product()
    {
        await WhenACatalogProductIsCreated("Widget", 9.99m);

        ThenTheResponseStatusIs(201);
        ThenTheCatalogProductIdIsReturned();
        await ThenTheStoredCatalogProductIs("Widget", 9.99m);
    }

    [BobcatSpec]
    public async Task get_catalog_products()
    {
        await GivenACatalogProduct("Gadget", 19.99m);

        await WhenAllCatalogProductsAreRead();

        ThenTheResponseStatusIs(200);
        ThenAtLeastCatalogProductsAreReturned(1);
    }

    [BobcatSpec]
    public async Task get_catalog_product_by_id()
    {
        await GivenACatalogProduct("Gizmo", 5.99m);

        await WhenTheCatalogProductIsRead();

        ThenTheResponseStatusIs(200);
    }

    [BobcatSpec]
    public async Task get_catalog_product_by_id_404()
    {
        await WhenTheCatalogProductWithIdIsRead(Guid.Empty);

        ThenTheResponseStatusIs(404);
    }

    [BobcatSpec]
    public async Task update_catalog_product()
    {
        await GivenACatalogProduct("OldWidget", 1.00m);

        await WhenTheCatalogProductIsRenamed("NewWidget");

        ThenTheResponseStatusIs(200);
        await ThenTheStoredCatalogProductIs("NewWidget", 1.00m);
    }

    [BobcatSpec]
    public async Task delete_catalog_product()
    {
        await GivenACatalogProduct("DeleteMe", 1.00m);

        await WhenTheCatalogProductIsDeleted();

        ThenTheResponseStatusIs(204);
        await ThenTheCatalogProductIsGone();
    }
}
