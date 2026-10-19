using Alba;
using Basket;
using Bobcat.CritterStack;
using Catalog;
using Discount;
using Ordering;
using Wolverine;
using Wolverine.Tracking;

namespace EcommerceModularMonolith.Tests;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The base for every EcommerceModularMonolith spec: the HTTP calls a step makes, over Alba, and an empty store before
/// every spec.
/// </summary>
/// <remarks>
/// The status is surfaced rather than asserted. Alba checks for a 200 unless told otherwise, and a spec
/// asserts whatever status it expects, so a 404 a spec is testing for must not fail the call that
/// produced it.
/// </remarks>
public abstract class EcommerceModularMonolithSpec(EcommerceModularMonolithHost app) : IAsyncLifetime
{
    protected IAlbaHost Alba => app.Host;

    // Empties every module's schema, and removes the host's seed data (three products, two coupons) so "at least 1 catalog product" means the one the spec created. Load-bearing for the ordering specs, which would otherwise find orders earlier runs left behind.
    public async ValueTask InitializeAsync() => await app.Host.ResetEventStoresAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected Task<HttpResult<T>> PostJsonAsync<TRequest, T>(string url, TRequest body)
        => SendAsync<T>(s => s.Post.Json(body!).ToUrl(url));

    protected Task<HttpResult<T>> PutJsonAsync<TRequest, T>(string url, TRequest body)
        => SendAsync<T>(s => s.Put.Json(body!).ToUrl(url));

    protected Task<HttpResult<T>> GetJsonAsync<T>(string url)
        => SendAsync<T>(s => s.Get.Url(url));

    protected Task<HttpResult<object>> DeleteAsync(string url)
        => SendAsync<object>(s => s.Delete.Url(url));

    protected async Task<HttpResult<T>> SendAsync<T>(Action<Scenario> configure)
    {
        var result = await Alba.Scenario(s =>
        {
            configure(s);
            s.IgnoreStatusCode();
        });

        T? body = default;
        try { body = await result.ReadAsJsonAsync<T>(); } catch { /* not every response carries JSON */ }

        return new HttpResult<T>(result.Context.Response.StatusCode, body);
    }

    /// <summary>
    /// Run an HTTP call and wait for every message it cascades to be fully handled. The modules in this
    /// sample talk over <c>UseDurableInbox()</c> local queues, so the response goes out before the next
    /// module has handled the cascade; asserting straight off it would race the handler.
    /// </summary>
    protected async Task<HttpResult<T>> TrackedAsync<T>(Func<Task<HttpResult<T>>> call)
    {
        HttpResult<T>? captured = null;

        // Explicitly typed: ExecuteAndWaitAsync overloads on Task and ValueTask, and an async lambda is
        // convertible to both.
        Func<IMessageContext, Task> act = async _ => { captured = await call(); };

        await Alba.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(act);

        return captured!;
    }

    // ---- the Ecommerce vocabulary ------------------------------------------------------------
    //
    // Shared by the four module spec classes. Reuses the host's own command and document types via the
    // project reference rather than re-declaring request records locally.

    private Guid _productId;
    private string _productName = string.Empty;
    private decimal _productPrice;
    private Guid _couponId;
    private string _couponProductName = string.Empty;
    private Guid _orderId;
    private int _lastStatusCode;
    private ShoppingCart? _basket;
    private Coupon? _coupon;
    private List<Product> _products = [];
    private List<OrderDto> _orders = [];

    // The checkout command wants a customer Guid, which the specs never name -- the basket is keyed by
    // user name. One customer per spec is what ties "the order created by the checkout" back to the
    // checkout that created it.
    private readonly Guid _customerId = Guid.NewGuid();

    // ---- catalog ----------------------------------------------------------------------------

    [Given("a catalog product named \"{name}\" priced {price}")]
    public Task GivenACatalogProduct(string name, decimal price) => createProduct(name, price);

    [When("a catalog product named \"{name}\" priced {price} is created")]
    public Task WhenACatalogProductIsCreated(string name, decimal price) => createProduct(name, price);

    private async Task createProduct(string name, decimal price)
    {
        // Category is required by the command's validator; the specs do not care which.
        var result = await PostJsonAsync<CreateProduct, Product>(
            "/products", new CreateProduct(name, ["Specs"], $"{name} description", $"{name}.png", price));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
        {
            _productId = result.Body.Id;
            _productName = result.Body.Name;
            _productPrice = result.Body.Price;
        }
    }

    [When("all catalog products are read")]
    public async Task WhenAllCatalogProductsAreRead()
    {
        var result = await GetJsonAsync<List<Product>>("/products");
        _lastStatusCode = result.StatusCode;
        _products = result.Body ?? [];
    }

    [When("the catalog product is read by id")]
    public Task WhenTheCatalogProductIsRead() => readProduct(_productId);

    [When("the catalog product with id {id} is read")]
    public Task WhenTheCatalogProductWithIdIsRead(Guid id) => readProduct(id);

    private async Task readProduct(Guid id)
    {
        var result = await GetJsonAsync<Product>($"/products/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("the catalog product is renamed \"{newName}\"")]
    public async Task WhenTheCatalogProductIsRenamed(string newName)
    {
        // PUT /products with the id in the body; [Entity] loads the Product from the command's Id. The
        // other fields are replayed from the create, because the command replaces the whole document.
        var result = await PutJsonAsync<UpdateProduct, Product>(
            "/products",
            new UpdateProduct(_productId, newName, ["Specs"], $"{newName} description", $"{newName}.png", _productPrice));

        _lastStatusCode = result.StatusCode;
    }

    [When("the catalog product is deleted")]
    public async Task WhenTheCatalogProductIsDeleted()
    {
        var result = await DeleteAsync($"/products/{_productId}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the catalog product id is returned")]
    public void ThenTheCatalogProductIdIsReturned() => SpecAssert.Fact(_productId != Guid.Empty, "No product id came back.");

    /// <summary>Reads the product back, so an update that answered 200 without persisting still fails.</summary>
    [Then("the stored catalog product is named \"{name}\" priced {price}")]
    public async Task ThenTheStoredCatalogProductIs(string name, decimal price)
    {
        var product = (await GetJsonAsync<Product>($"/products/{_productId}")).Body;
        SpecAssert.Check("name", product?.Name, name);
        SpecAssert.Check("price", product?.Price, price);
    }

    [Then("at least {count} catalog product is returned")]
    public void ThenAtLeastCatalogProductsAreReturned(int count)
        => SpecAssert.Fact(_products.Count >= count, $"Expected at least {count} product(s), got {_products.Count}.");

    [Then("the catalog product is gone")]
    public async Task ThenTheCatalogProductIsGone()
        => SpecAssert.Check("status", (await GetJsonAsync<Product>($"/products/{_productId}")).StatusCode, 404);

    // ---- basket -----------------------------------------------------------------------------

    [Given("a basket stored for customer \"{userName}\" with the product")]
    public Task GivenABasket(string userName) => storeBasket(userName);

    [When("a basket is stored for customer \"{userName}\" with the product")]
    public Task WhenABasketIsStored(string userName) => storeBasket(userName);

    private async Task storeBasket(string userName)
    {
        // The host's own ShoppingCart document, keyed by user name, holding the product the spec just
        // created at the price it was created with.
        var cart = new ShoppingCart
        {
            Id = userName,
            Items =
            [
                new ShoppingCartItem
                {
                    ProductId = _productId,
                    ProductName = _productName,
                    Quantity = 1,
                    Price = _productPrice,
                    Color = "Black",
                },
            ],
        };

        var result = await PostJsonAsync<StoreBasket, ShoppingCart>("/basket", new StoreBasket(cart));
        _lastStatusCode = result.StatusCode;
        _basket = result.Body;
    }

    [When("the basket for customer \"{userName}\" is read")]
    public async Task WhenTheBasketIsRead(string userName)
    {
        var result = await GetJsonAsync<ShoppingCart>($"/basket/{userName}");
        _lastStatusCode = result.StatusCode;
        _basket = result.Body;
    }

    [When("the basket for customer \"{userName}\" is deleted")]
    public async Task WhenTheBasketIsDeleted(string userName)
    {
        var result = await DeleteAsync($"/basket/{userName}");
        _lastStatusCode = result.StatusCode;
    }

    [Given("the basket for customer \"{userName}\" was checked out")]
    public Task GivenTheBasketWasCheckedOut(string userName) => checkout(userName);

    [When("the basket for customer \"{userName}\" is checked out")]
    public Task WhenTheBasketIsCheckedOut(string userName) => checkout(userName);

    private async Task checkout(string userName)
    {
        var command = new CheckoutBasket(
            userName,
            _customerId,
            "Jane", "Doe", "jane@example.com", "1 Main St", "US", "TX", "75001",
            "Jane Doe", "4111111111111111", "12/30", "123", PaymentMethod: 1);

        // Checkout returns 202 before the Ordering module has handled BasketCheckoutEvent, so the call is
        // tracked until the cascade is fully handled.
        var result = await TrackedAsync(() => PostJsonAsync<CheckoutBasket, object>("/basket/checkout", command));
        _lastStatusCode = result.StatusCode;
    }

    [Then("the basket total is {total}")]
    public void ThenTheBasketTotalIs(decimal total) => SpecAssert.Check("total", _basket?.TotalPrice, total);

    [Then("the basket for customer \"{userName}\" is gone")]
    public async Task ThenTheBasketIsGone(string userName)
        => SpecAssert.Check("status", (await GetJsonAsync<ShoppingCart>($"/basket/{userName}")).StatusCode, 404);

    // ---- ordering ---------------------------------------------------------------------------

    [When("all orders are read")]
    public async Task WhenAllOrdersAreRead()
    {
        var result = await GetJsonAsync<List<OrderDto>>("/orders");
        _lastStatusCode = result.StatusCode;
        _orders = result.Body ?? [];
    }

    [When("the order created by the checkout is read")]
    public async Task WhenTheCheckoutOrderIsRead()
    {
        await locateCheckoutOrder();
        var result = await GetJsonAsync<OrderDto>($"/orders/{_orderId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("the order created by the checkout is deleted")]
    public async Task WhenTheCheckoutOrderIsDeleted()
    {
        await locateCheckoutOrder();
        var result = await DeleteAsync($"/orders/{_orderId}");
        _lastStatusCode = result.StatusCode;
    }

    // Looked up by customer rather than "the first order in the list", so the step cannot pick up an
    // order another spec, or another run, left behind.
    private async Task locateCheckoutOrder()
        => _orderId = (await GetJsonAsync<List<OrderDto>>($"/orders/customer/{_customerId}")).Body?.FirstOrDefault()?.Id ?? Guid.Empty;

    [Then("at least {count} order is returned")]
    public void ThenAtLeastOrdersAreReturned(int count)
        => SpecAssert.Fact(_orders.Count >= count, $"Expected at least {count} order(s), got {_orders.Count}.");

    /// <summary>
    /// The sample's central claim: a checkout in the Basket module becomes an order in the Ordering module,
    /// carried over a durable local queue. The customer id is the thread that ties the two together.
    /// </summary>
    [Then("an order exists for the checked-out customer")]
    public void ThenAnOrderExistsForTheCheckedOutCustomer()
        => SpecAssert.Fact(_orders.Any(o => o.CustomerId == _customerId), $"No order for customer {_customerId}.");

    // ---- discount ---------------------------------------------------------------------------

    [Given("a discount for product \"{productName}\" of {amount}")]
    public Task GivenADiscount(string productName, decimal amount) => createDiscount(productName, amount);

    [When("a discount for product \"{productName}\" of {amount} is created")]
    public Task WhenADiscountIsCreated(string productName, decimal amount) => createDiscount(productName, amount);

    private async Task createDiscount(string productName, decimal amount)
    {
        var result = await PostJsonAsync<CreateCoupon, Coupon>(
            "/discounts", new CreateCoupon(productName, $"{productName} discount", amount));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
        {
            _couponId = result.Body.Id;
            _couponProductName = result.Body.ProductName;
        }
    }

    [When("the discount for product \"{productName}\" is read")]
    public async Task WhenTheDiscountIsRead(string productName)
    {
        var result = await GetJsonAsync<Coupon>($"/discounts/{productName}");
        _lastStatusCode = result.StatusCode;
        _coupon = result.Body;
    }

    [When("the discount is changed to {amount}")]
    public async Task WhenTheDiscountIsChanged(decimal amount)
    {
        // PUT /discounts with the id in the body; [Entity] loads the Coupon from UpdateCoupon.Id.
        var result = await PutJsonAsync<UpdateCoupon, Coupon>(
            "/discounts", new UpdateCoupon(_couponId, _couponProductName, $"{_couponProductName} discount", amount));

        _lastStatusCode = result.StatusCode;
    }

    [When("the discount is deleted")]
    public async Task WhenTheDiscountIsDeleted()
    {
        var result = await DeleteAsync($"/discounts/{_couponId}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the discount amount is {amount}")]
    public void ThenTheDiscountAmountIs(decimal amount) => SpecAssert.Check("amount", _coupon?.Amount, amount);

    [Then("the stored discount for product \"{productName}\" has amount {amount}")]
    public async Task ThenTheStoredDiscountIs(string productName, decimal amount)
        => SpecAssert.Check("amount", (await GetJsonAsync<Coupon>($"/discounts/{productName}")).Body?.Amount, amount);

    // ---- shared -----------------------------------------------------------------------------

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);
}
