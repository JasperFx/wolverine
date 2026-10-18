namespace Shared;

/// <summary>
/// Integration event published by Basket service when checkout completes.
/// Consumed by the Ordering service to create a new order.
/// Replaces MassTransit's BasketCheckoutEvent + BuildingBlocks.Messaging.
/// </summary>
public record BasketCheckoutEvent(
    string UserName,
    Guid CustomerId,
    decimal TotalPrice,
    // Shipping
    string FirstName,
    string LastName,
    string EmailAddress,
    string AddressLine,
    string Country,
    string State,
    string ZipCode,
    // Payment
    string CardName,
    string CardNumber,
    string Expiration,
    string CVV,
    int PaymentMethod
);
