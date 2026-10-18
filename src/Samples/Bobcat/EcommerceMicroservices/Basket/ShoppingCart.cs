namespace Basket;

public class ShoppingCart
{
    // UserName as the identity — Marten supports string IDs
    public string Id { get; set; } = string.Empty;
    public string UserName => Id;
    public List<ShoppingCartItem> Items { get; set; } = [];
    public decimal TotalPrice => Items.Sum(i => i.Price * i.Quantity);
}

public class ShoppingCartItem
{
    public int Quantity { get; set; }
    public string Color { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
}
