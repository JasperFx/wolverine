using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
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
    // Created rather than a bare Coupon — see docs/sample-wiring.md footgun 3 for why it is
    // returned directly and not as a (Coupon, IResult) tuple.
    [WolverinePost("/discounts")]
    public static Created<Coupon> Post(CreateCoupon command, IDocumentSession session)
    {
        var coupon = new Coupon
        {
            Id = Guid.NewGuid(),
            ProductName = command.ProductName,
            Description = command.Description,
            Amount = command.Amount,
        };
        session.Store(coupon);
        return TypedResults.Created($"/discounts/{coupon.ProductName}", coupon);
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
