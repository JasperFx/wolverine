using System.Collections.Concurrent;
using JasperFx.Core;
using MartenTests.Workflows.Engine;
using Wolverine.Persistence.Sagas;

namespace MartenTests.Workflows;

// GH-5654 SPIKE: the #5405 PlaceOrder example, plus OnFailure registrations and one RunAsync for the I/O

public record PlaceOrder([property: SagaIdentity] Guid OrderId, string Address);

public record ReserveInventory(Guid OrderId);

// No workflow id member on purpose: the reply finds its workflow through the saga-id header
public record InventoryReserved(string Warehouse);

// Sent from outside the workflow, so it has to name the workflow itself
public record PaymentReceived([property: SagaIdentity] Guid OrderId, decimal Amount);

public record ReleaseInventory(Guid OrderId);

public record RefundPayment(Guid OrderId, decimal Amount);

public record ShipOrder(Guid OrderId, string Label);

public record ChargeCard(Guid OrderId);

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

/// <summary>
/// The same workflow name and version as <see cref="PlaceOrderWorkflow" /> after someone changed it in place:
/// it now charges the card before reserving inventory. Instances started on the old code can't replay on it.
/// </summary>
public static class ChangedPlaceOrderWorkflow
{
    [DurableWorkflow("PlaceOrderWorkflow.Run")]
    public static async Task Run(PlaceOrder order, IWorkflowContext ctx, IShippingLabels labels)
    {
        ctx.OnFailure(new ReleaseInventory(order.OrderId));
        await ctx.SendAsync(new ChargeCard(order.OrderId));
        var reserved = await ctx.InvokeAsync<InventoryReserved>(new ReserveInventory(order.OrderId));

        var paid = await ctx.WaitForAsync<PaymentReceived>(timeout: 1.Days());
        if (paid is null) return;

        var label = await ctx.RunAsync(() => labels.CreateLabelAsync(order.OrderId, order.Address));
        await ctx.SendAsync(new ShipOrder(order.OrderId, $"{label} from {reserved.Warehouse}"));
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

    public static void Handle(ChargeCard command)
    {
    }
}
