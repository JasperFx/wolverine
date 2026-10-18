using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Ordering;

public static class GetOrdersEndpoint
{
    [WolverineGet("/orders")]
    public static async Task<IReadOnlyList<OrderDto>> Get(IQuerySession session, CancellationToken ct)
    {
        var orders = await session.Query<Order>()
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

        return orders.Select(o => new OrderDto(o.Id, o.CustomerId, o.OrderName, o.TotalPrice, o.Status, o.Items)).ToList();
    }
}

public static class GetOrderByIdEndpoint
{
    [WolverineGet("/orders/{id}")]
    public static OrderDto? Get(Guid id, [Entity] Order? order)
        => order is not null
            ? new OrderDto(order.Id, order.CustomerId, order.OrderName, order.TotalPrice, order.Status, order.Items)
            : null;
}

public static class GetOrdersByCustomerEndpoint
{
    [WolverineGet("/orders/customer/{customerId}")]
    public static async Task<IReadOnlyList<OrderDto>> Get(Guid customerId, IQuerySession session, CancellationToken ct)
    {
        var orders = await session.Query<Order>()
            .Where(o => o.CustomerId == customerId)
            .ToListAsync(ct);

        return orders.Select(o => new OrderDto(o.Id, o.CustomerId, o.OrderName, o.TotalPrice, o.Status, o.Items)).ToList();
    }
}
