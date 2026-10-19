using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Discount;

public record CreateCoupon(string ProductName, string Description, decimal Amount);
public record UpdateCoupon(Guid Id, string ProductName, string Description, decimal Amount);

public static class GetCouponEndpoint
{
    [WolverineGet("/discounts/{productName}")]
    public static async Task<Coupon?> Get(string productName, IQuerySession session, CancellationToken ct)
        => await session.Query<Coupon>().FirstOrDefaultAsync(c => c.ProductName == productName, ct);
}

public static class CreateCouponEndpoint
{
    [WolverinePost("/discounts")]
    public static Coupon Post(CreateCoupon command, IDocumentSession session)
    {
        var coupon = new Coupon
        {
            Id = Guid.NewGuid(),
            ProductName = command.ProductName,
            Description = command.Description,
            Amount = command.Amount,
        };
        session.Store(coupon);
        return coupon;
    }
}

public static class UpdateCouponEndpoint
{
    [WolverinePut("/discounts")]
    public static Coupon Put(UpdateCoupon command, [Entity(Required = true)] Coupon coupon, IDocumentSession session)
    {
        coupon.ProductName = command.ProductName;
        coupon.Description = command.Description;
        coupon.Amount = command.Amount;
        session.Store(coupon);
        return coupon;
    }
}

public static class DeleteCouponEndpoint
{
    [WolverineDelete("/discounts/{id}")]
    public static void Delete(Guid id, [Entity(Required = true)] Coupon coupon, IDocumentSession session)
    {
        session.Delete(coupon);
    }
}
