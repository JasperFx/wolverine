using Catalog;

namespace EcommerceMicroservices.Tests;

/// <summary>
/// The Catalog service's product endpoints, driven over HTTP. The specs reuse the service's own command
/// and document types via the project reference, so they cannot drift from the API's shape.
/// </summary>
[BobcatFeature("Ecommerce Microservices Products")]
public class ProductSpecs(EcommerceMicroservicesHost app) : EcommerceMicroservicesSpec(app)
{
    private Guid _productId;
    private int _lastStatusCode;
    private Product? _product;
    private List<Product> _products = [];

    [BobcatSpec]
    public async Task create_a_product()
    {
        await WhenAProductIsCreated("Widget", "Tools", 9.99m);

        ThenTheResponseStatusIs(200);
        ThenTheProductIdIsReturned();
    }

    [BobcatSpec]
    public async Task create_product_with_invalid_data_returns_400()
    {
        await WhenAProductIsCreated("", "Category", 1.0m);

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task get_product_list()
    {
        await GivenAProduct("Gadget", "Electronics", 29.99m);

        await WhenAllProductsAreRead();

        ThenAtLeastProductsAreReturned(1);
    }

    [BobcatSpec]
    public async Task get_product_by_id()
    {
        await GivenAProduct("Gizmo", "Electronics", 14.99m);

        await WhenTheProductIsRead(_productId);

        ThenTheResponseStatusIs(200);
        ThenTheProductNameIs("Gizmo");
    }

    [BobcatSpec]
    public async Task get_product_by_id_returns_404_for_missing()
    {
        await WhenTheProductIsRead(Guid.Empty);

        ThenTheResponseStatusIs(404);
    }

    [BobcatSpec]
    public async Task get_products_by_category()
    {
        await GivenAProduct("Sprocket", "Parts", 4.99m);

        await WhenTheProductsInCategoryAreRead("Parts");

        ThenAtLeastProductsAreReturned(1);
    }

    [BobcatSpec]
    public async Task update_a_product()
    {
        await GivenAProduct("OldName", "Misc", 1.00m);

        await WhenTheProductIsUpdated("NewName", 2.00m);

        ThenTheResponseStatusIs(200);
    }

    [BobcatSpec]
    public async Task update_product_with_invalid_data_returns_400()
    {
        await GivenAProduct("ValidProduct", "Misc", 1.00m);

        await WhenTheProductIsUpdated("", 1.00m);

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task delete_a_product()
    {
        await GivenAProduct("ToDelete", "Misc", 1.00m);

        await WhenTheProductIsDeleted(_productId);

        ThenTheResponseStatusIs(204);
    }

    [BobcatSpec]
    public async Task delete_returns_404_for_missing_product()
    {
        await WhenTheProductIsDeleted(Guid.Empty);

        ThenTheResponseStatusIs(404);
    }

    [BobcatSpec]
    public async Task delete_nonexistent_product_returns_404()
    {
        await GivenAProduct("Ephemeral", "Misc", 1.00m);
        await GivenTheProductWasDeleted();

        await WhenTheProductIsDeleted(_productId);

        ThenTheResponseStatusIs(404);
    }

    // ---- steps ------------------------------------------------------------------------------

    [Given("a product named \"{name}\" in category \"{category}\" priced {price}")]
    public Task GivenAProduct(string name, string category, decimal price) => createProduct(name, category, price);

    [When("a product named \"{name}\" in category \"{category}\" priced {price} is created")]
    public Task WhenAProductIsCreated(string name, string category, decimal price) => createProduct(name, category, price);

    private async Task createProduct(string name, string category, decimal price)
    {
        var result = await PostJsonAsync<CreateProduct, Product>(
            "/products", new CreateProduct(name, [category], $"{name} description", $"{name}.png", price));

        _lastStatusCode = result.StatusCode;
        if (result.StatusCode is >= 200 and < 300 && result.Body is not null) _productId = result.Body.Id;
    }

    [When("all products are read")]
    public async Task WhenAllProductsAreRead()
    {
        var result = await GetJsonAsync<List<Product>>("/products");
        _lastStatusCode = result.StatusCode;
        _products = result.Body ?? [];
    }

    [When("the product with id {id} is read")]
    public async Task WhenTheProductIsRead(Guid id)
    {
        var result = await GetJsonAsync<Product>($"/products/{id}");
        _lastStatusCode = result.StatusCode;
        _product = result.Body;
    }

    [When("the products in category \"{category}\" are read")]
    public async Task WhenTheProductsInCategoryAreRead(string category)
    {
        var result = await GetJsonAsync<List<Product>>($"/products/category/{category}");
        _lastStatusCode = result.StatusCode;
        _products = result.Body ?? [];
    }

    [When("the product is updated to name \"{name}\" priced {price}")]
    public async Task WhenTheProductIsUpdated(string name, decimal price)
    {
        // PUT /products with the id in the body; [Entity] loads the Product from the command's Id.
        var result = await PutJsonAsync<UpdateProduct, Product>(
            "/products", new UpdateProduct(_productId, name, ["Misc"], $"{name} description", $"{name}.png", price));

        _lastStatusCode = result.StatusCode;
    }

    [Given("the product was deleted")]
    public Task GivenTheProductWasDeleted() => deleteProduct(_productId);

    [When("the product with id {id} is deleted")]
    public Task WhenTheProductIsDeleted(Guid id) => deleteProduct(id);

    private async Task deleteProduct(Guid id)
    {
        var result = await DeleteAsync($"/products/{id}");
        _lastStatusCode = result.StatusCode;
    }

    // #region response-status
    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);
    // #endregion response-status

    [Then("the product id is returned")]
    public void ThenTheProductIdIsReturned() => SpecAssert.Fact(_productId != Guid.Empty, "No product id came back.");

    [Then("at least {count} product is returned")]
    public void ThenAtLeastProductsAreReturned(int count)
        => SpecAssert.Fact(_products.Count >= count, $"Expected at least {count} product(s), got {_products.Count}.");

    [Then("the product name is \"{name}\"")]
    public void ThenTheProductNameIs(string name) => SpecAssert.Check("name", _product?.Name, name);
}
