using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Wolverine.Http.Tests.EfCoreOnly;

// Reproducers for GH-3374: an [AsParameters] endpoint whose compound-handler LoadAsync
// binds the same route variable made the runtime codegen emit the route-binding frames
// once per consuming method scope. The duplicated locals (order_id_rawValue / order_id,
// and for the [AsParameters]-on-LoadAsync variant the request variable itself) meant the
// generated class did not compile, so the host failed at startup.

public class Bug3374DbContext : DbContext
{
    public Bug3374DbContext(DbContextOptions<Bug3374DbContext> options) : base(options)
    {
    }

    public DbSet<Bug3374Order> Orders => Set<Bug3374Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Bug3374Order>(map =>
        {
            map.ToTable("bug3374_orders", "bug3374");
            map.HasKey(x => x.Id);
        });
    }
}

public class Bug3374Order
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class Bug3374UpsertLinesRequest
{
    [FromRoute(Name = "order-id")]
    public long OrderId { get; set; }

    [FromBody]
    public Bug3374LinesBody Body { get; set; } = new([]);
}

public sealed record Bug3374LinesBody(List<string> Lines);

public sealed record Bug3374Response(long OrderId, string OrderName, int LineCount);

// Variant 1 from GH-3374: LoadAsync binds the raw route value while the HTTP handler
// binds the same route value through an [AsParameters] container.
public static class Bug3374RouteLoadEndpoint
{
    public static async Task<Bug3374Order> LoadAsync(
        [FromRoute(Name = "order-id")] long orderId,
        Bug3374DbContext dbContext,
        CancellationToken cancellationToken)
        => await dbContext.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
           ?? throw new InvalidOperationException($"No order with id {orderId}");

    [WolverinePut("/bug3374/route-load/orders/{order-id:long}/lines")]
    public static Bug3374Response Put(
        [AsParameters] Bug3374UpsertLinesRequest request,
        [NotBody] Bug3374Order order,
        [NotBody] Bug3374DbContext dbContext,
        CancellationToken cancellationToken)
    {
        return new Bug3374Response(request.OrderId, order.Name, request.Body.Lines.Count);
    }
}

// Variant 2 from GH-3374: LoadAsync binds the same [AsParameters] container as the HTTP
// handler, which additionally collided on the container variable itself.
public static class Bug3374AsParametersLoadEndpoint
{
    public static async Task<Bug3374Order> LoadAsync(
        [AsParameters] Bug3374UpsertLinesRequest request,
        Bug3374DbContext dbContext,
        CancellationToken cancellationToken)
        => await dbContext.Orders.FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken)
           ?? throw new InvalidOperationException($"No order with id {request.OrderId}");

    [WolverinePut("/bug3374/asparameters-load/orders/{order-id:long}/lines")]
    public static Bug3374Response Put(
        [AsParameters] Bug3374UpsertLinesRequest request,
        [NotBody] Bug3374Order order,
        [NotBody] Bug3374DbContext dbContext,
        CancellationToken cancellationToken)
    {
        return new Bug3374Response(request.OrderId, order.Name, request.Body.Lines.Count);
    }
}
