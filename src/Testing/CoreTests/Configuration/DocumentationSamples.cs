using JasperFx.Core;
using Lamar;
using Lamar.Microsoft.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

public static class DocumentationSamples
{
    public static async Task bootstrap_with_lamar()
    {
        #region sample_use_lamar_with_host_builder

        // With IHostBuilder
        var builder = Host.CreateDefaultBuilder();
        builder.UseLamar();

        #endregion
        
        
    }

    public static async Task bootstrap_with_lamar_using_web_app()
    {
        var builder = Host.CreateApplicationBuilder();
        
        // Little ugly, and Lamar *should* have a helper for this...
        builder.ConfigureContainer<ServiceRegistry>(new LamarServiceProviderFactory());
    }


    #region sample_using_tracked_sessions_end_to_end

    // Personally, I prefer to reuse the IHost between tests and
    // do something to clear off any dirty state, but other folks
    // will spin up an IHost per test to maybe get better test parallelization
    public static async Task run_end_to_end(IHost host)
    {
        var placeOrder = new PlaceOrder("111", "222", 1000);
        
        // This would be the "act" part of your arrange/act/assert
        // test structure
        var tracked = await host.InvokeMessageAndWaitAsync(placeOrder);
        
        // proceed to test the outcome of handling the original command *and*
        // any subsequent domain events that are published from the original
        // command handler
    }

    #endregion

    #region sample_test_specific_queue_end_to_end

    public static async Task test_specific_handler(IHost host)
    {
        // We're not thrilled with this usage and it's possible there's
        // syntactic sugar additions to the API soon
        await host.ExecuteAndWaitAsync(
            c => c.EndpointFor("local queue name").SendAsync(new OrderPlaced("111")).AsTask());
    }

    #endregion

    #region sample_using_external_brokers_with_tracked_sessions

    public static async Task run_end_to_end_with_external_transports(IHost host)
    {
        var placeOrder = new PlaceOrder("111", "222", 1000);
        
        // This would be the "act" part of your arrange/act/assert
        // test structure
        var tracked = await host
            .TrackActivity()
            
            // Direct Wolverine to also track activity coming and going from
            // external brokers
            .IncludeExternalTransports()
            
            // You'll sadly need to do this sometimes
            .Timeout(30.Seconds())
            
            // You *might* have to do this as well to make
            // your tests more reliable in the face of async messaging
            .WaitForMessageToBeReceivedAt<OrderPlaced>(host)
            
            .InvokeMessageAndWaitAsync(placeOrder);
        
        // proceed to test the outcome of handling the original command *and*
        // any subsequent domain events that are published from the original
        // command handler
    }

    #endregion
}


public record PlaceOrder(
    string OrderId,
    string CustomerId,
    decimal Amount
);

public record OrderPlaced(string OrderId);