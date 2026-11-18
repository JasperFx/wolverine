using System.Collections.Concurrent;
using JasperFx.Core;
using Wolverine.Persistence.Sagas;
using Wolverine.Workflows;

namespace MartenTests.Workflows;

// GH-5660 SPIKE: the #5405 PlaceOrder example (plus two OnFailure registrations and the label I/O through RunAsync),
// compiled into PlaceOrderWorkflowSaga by Wolverine.SourceGeneration. The generated file is written to
// obj/<config>/<tfm>/generated/Wolverine.SourceGeneration/.../MartenTests.Workflows.PlaceOrderWorkflowSaga.g.cs

public record PlaceOrder([property: SagaIdentity] Guid OrderId, string Address);

public record ReserveInventory(Guid OrderId);

// No workflow id member on purpose: the reply finds its workflow through the saga-id header
public record InventoryReserved(string Warehouse);

// Sent from outside the workflow, so it has to name the workflow itself
public record PaymentReceived([property: SagaIdentity] Guid OrderId, decimal Amount);

public record ReleaseInventory(Guid OrderId);

public record RefundPayment(Guid OrderId, decimal Amount);

public record ShipOrder(Guid OrderId, string Label);

public static class PlaceOrderWorkflow
{
    [DurableWorkflow]
    public static async Task Run(PlaceOrder order, IWorkflowContext ctx, IShippingLabels labels)
    {
        ctx.OnFailure(new ReleaseInventory(order.OrderId));
        var reserved = await ctx.InvokeAsync<InventoryReserved>(new ReserveInventory(order.OrderId));

        var paid = await ctx.WaitForAsync<PaymentReceived>(timeout: 1.Days());
        if (paid is null)
        {
            await ctx.SendAsync(new ReleaseInventory(order.OrderId));
            return;
        }

        ctx.OnFailure(new RefundPayment(order.OrderId, paid.Amount));
        var label = await ctx.RunAsync(() => labels.CreateLabelAsync(order.OrderId, order.Address));
        await ctx.SendAsync(new ShipOrder(order.OrderId, $"{label} from {reserved.Warehouse}"));
    }
}

// A second workflow for the constructs PlaceOrder doesn't have: a wait inside an if, a local assigned in one branch
// and read after the if, and DelayAsync

public record ReviewOrder([property: SagaIdentity] Guid OrderId, decimal Amount);

public record RequestApproval(Guid OrderId, decimal Amount);

public record ManagerApproved([property: SagaIdentity] Guid OrderId, string Manager);

public record ReviewCompleted(Guid OrderId, string Decision);

public static class ReviewOrderWorkflow
{
    public const decimal ApprovalLimit = 1000m;

    [DurableWorkflow]
    public static async Task Run(ReviewOrder review, IWorkflowContext ctx)
    {
        var decision = "approved automatically";
        if (review.Amount > ApprovalLimit)
        {
            await ctx.SendAsync(new RequestApproval(review.OrderId, review.Amount));
            var approval = await ctx.WaitForAsync<ManagerApproved>(4.Hours());
            decision = approval is null ? "rejected, no answer in time" : $"approved by {approval.Manager}";
        }

        // A cooling-off period before the decision goes out, whichever way the if went
        await ctx.DelayAsync(5.Minutes());
        await ctx.SendAsync(new ReviewCompleted(review.OrderId, decision));
    }
}

public interface IShippingLabels
{
    Task<string> CreateLabelAsync(Guid orderId, string address);
}

public class ShippingLabels : IShippingLabels
{
    public static readonly ConcurrentDictionary<Guid, int> Calls = new();

    public Task<string> CreateLabelAsync(Guid orderId, string address)
    {
        Calls.AddOrUpdate(orderId, 1, (_, count) => count + 1);

        if (address == "nowhere") throw new InvalidOperationException("No carrier delivers to nowhere");

        return Task.FromResult($"LABEL-{address.ToUpperInvariant()}");
    }
}

public static class InventoryHandler
{
    /// <summary>Orders whose reservation reply the test sends by hand</summary>
    public static readonly ConcurrentDictionary<Guid, bool> HeldReplies = new();

    public static InventoryReserved? Handle(ReserveInventory command)
    {
        return HeldReplies.ContainsKey(command.OrderId) ? null : new InventoryReserved("Main");
    }
}

public static class OrderSinkHandler
{
    public static void Handle(ReleaseInventory command)
    {
    }

    public static void Handle(RefundPayment command)
    {
    }

    public static void Handle(ShipOrder command)
    {
    }

    public static void Handle(RequestApproval command)
    {
    }

    public static void Handle(ReviewCompleted command)
    {
    }
}
