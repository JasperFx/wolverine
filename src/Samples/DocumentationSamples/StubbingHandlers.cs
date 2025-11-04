using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Tracking;

namespace DocumentationSamples;

public class StubbingHandlers
{
    public static async Task configure()
    {
        #region sample_configuring_estimate_delivery

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts
                .UseRabbitMq(builder.Configuration.GetConnectionString("rabbit"))
                .AutoProvision();

            // Just showing that EstimateDelivery is handled by
            // whatever system is on the other end of the "estimates" queue
            opts.PublishMessage<EstimateDelivery>()
                .ToRabbitQueue("estimates");
        });

        #endregion
    }

    #region sample_using_stub_handler_in_testing_code

    public static async Task try_application(IHost host)
    {
        host.StubWolverineMessageHandling<EstimateDelivery, DeliveryInformation>(
            query => new DeliveryInformation(new TimeOnly(17, 0), 1000));

        var locationId = Guid.NewGuid();
        var itemId = 111;
        var expectedDate = new DateOnly(2025, 12, 1);
        var postalCode = "78750";


        var maybePurchaseItem = new MaybePurchaseItem(itemId, locationId, expectedDate, postalCode,
            500);
        
        var tracked =
            await host.InvokeMessageAndWaitAsync(maybePurchaseItem);
        
        // The estimated cost from the stub was more than we budgeted
        // so this message should have been published
        
        // This line is an assertion too that there was a single message
        // of this type published as part of the message handling above
        var rejected = tracked.Sent.SingleMessage<PurchaseRejected>();
        rejected.ItemId.ShouldBe(itemId);
        rejected.LocationId.ShouldBe(locationId);
    }

    #endregion

    #region sample_clearing_out_stub_behavior

    public static void revert_stub(IHost host)
    {
        // Selectively clear out the stub behavior for only one message
        // type
        host.WolverineStubs(stubs =>
        {
            stubs.Clear<EstimateDelivery>();
        });
        
        // Or just clear out all active Wolverine message handler
        // stubs
        host.ClearAllWolverineStubs();
    }

    #endregion

    #region sample_override_previous_stub_behavior

    public static void override_stub(IHost host)
    {
        host.StubWolverineMessageHandling<EstimateDelivery, DeliveryInformation>(
            query => new DeliveryInformation(new TimeOnly(17, 0), 250));

    }

    #endregion

    #region sample_using_more_complex_stubs

    public static void more_complex_stub(IHost host)
    {
        host.WolverineStubs(stubs =>
        {
            stubs.Stub<EstimateDelivery>(async (
                EstimateDelivery message, 
                IMessageContext context, 
                IServiceProvider services,
                CancellationToken cancellation) =>
            {
                // do whatever you want, including publishing any number of messages
                // back through IMessageContext
                
                // And grab any other services you might need from the application 
                // through the IServiceProvider -- but note that you will have
                // to deal with scopes yourself here

                // This is an equivalent to get the response back to the 
                // original caller
                await context.PublishAsync(new DeliveryInformation(new TimeOnly(17, 0), 250));
            });
        });
    }

    #endregion
}

#region sample_code_showing_remote_request_reply

// This query message is normally sent to an external system through Wolverine
// messaging
public record EstimateDelivery(int ItemId, DateOnly Date, string PostalCode);

// This message type is a response from an external system
public record DeliveryInformation(TimeOnly DeliveryTime, decimal Cost);

public record MaybePurchaseItem(int ItemId, Guid LocationId, DateOnly Date, string PostalCode, decimal BudgetedCost);
public record MakePurchase(int ItemId, Guid LocationId, DateOnly Date);
public record PurchaseRejected(int ItemId, Guid LocationId, DateOnly Date);

public static class MaybePurchaseHandler
{
    public static Task<DeliveryInformation> LoadAsync(
        MaybePurchaseItem command, 
        IMessageBus bus, 
        CancellationToken cancellation)
    {
        var (itemId, _, date, postalCode, budget) = command;
        var estimateDelivery = new EstimateDelivery(itemId, date, postalCode);
        
        // Let's say this is doing a remote request and reply to another system
        // through Wolverine messaging
        return bus.InvokeAsync<DeliveryInformation>(estimateDelivery, cancellation);
    }
    
    public static object Handle(
        MaybePurchaseItem command, 
        DeliveryInformation estimate)
    {

        if (estimate.Cost <= command.BudgetedCost)
        {
            return new MakePurchase(command.ItemId, command.LocationId, command.Date);
        }

        return new PurchaseRejected(command.ItemId, command.LocationId, command.Date);
    }
}

#endregion