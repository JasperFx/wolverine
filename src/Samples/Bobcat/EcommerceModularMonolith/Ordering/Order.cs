namespace Ordering;

/// <summary>
/// Order document stored in Marten. Replaces the DDD aggregate with
/// ValueObjects (OrderId, CustomerId, Address, Payment) and the separate
/// Customer, Product, OrderItem entities from 4 EF Core configuration classes.
/// Marten stores this as a single JSON document — no joins needed.
/// </summary>
public class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string OrderName { get; set; } = string.Empty;

    // Shipping address (was Address ValueObject)
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;

    // Payment (was Payment ValueObject)
    public string CardName { get; set; } = string.Empty;
    public string CardNumber { get; set; } = string.Empty;
    public string Expiration { get; set; } = string.Empty;
    public string CVV { get; set; } = string.Empty;
    public int PaymentMethod { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public List<OrderItem> Items { get; set; } = [];
    public decimal TotalPrice => Items.Sum(i => i.Price * i.Quantity);

    // Audit
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class OrderItem
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public enum OrderStatus
{
    Draft,
    Pending,
    Completed,
    Cancelled,
}

/// <summary>
/// DTO for API responses — keeps internal document shape private.
/// </summary>
public record OrderDto(
    Guid Id,
    Guid CustomerId,
    string OrderName,
    decimal TotalPrice,
    OrderStatus Status,
    IReadOnlyList<OrderItem> Items
);
