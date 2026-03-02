using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Handlers;
using Wolverine.Shims.MassTransit;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Shims;

public class masstransit_end_to_end : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public masstransit_end_to_end(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.IncludeType<PlaceOrderConsumer>();
                opts.IncludeType<OrderPlacedCascadeHandler>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task consumer_handles_message_and_publishes_cascading_event()
    {
        PlaceOrderConsumer.Reset();
        OrderPlacedCascadeHandler.Reset();

        var session = await _host.InvokeMessageAndWaitAsync(new MtPlaceOrder("ORD-001"));

        PlaceOrderConsumer.LastOrderId.ShouldBe("ORD-001");
        OrderPlacedCascadeHandler.LastOrderId.ShouldBe("ORD-001");
    }

    [Fact]
    public async Task consumer_receives_message_metadata_via_consume_context()
    {
        PlaceOrderConsumer.Reset();

        await _host.InvokeMessageAndWaitAsync(new MtPlaceOrder("ORD-002"));

        PlaceOrderConsumer.LastOrderId.ShouldBe("ORD-002");
        PlaceOrderConsumer.ReceivedContext.ShouldBeTrue();
    }

    [Fact]
    public void print_generated_code_for_consumer()
    {
        // Force code generation by resolving the handler
        _host.GetRuntime().Handlers.HandlerFor<MtPlaceOrder>();

        var graph = _host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<MtPlaceOrder>();
        chain.ShouldNotBeNull();

        _output.WriteLine("=== Generated Code for MtPlaceOrder (MassTransit IConsumer<T>) ===");
        _output.WriteLine(chain.SourceCode);
    }

    [Fact]
    public void print_generated_code_for_cascaded_handler()
    {
        // Force code generation by resolving the handler
        _host.GetRuntime().Handlers.HandlerFor<MtOrderPlaced>();

        var graph = _host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<MtOrderPlaced>();
        chain.ShouldNotBeNull();

        _output.WriteLine("=== Generated Code for MtOrderPlaced cascade handler ===");
        _output.WriteLine(chain.SourceCode);
    }
}

// --- MassTransit shim message types ---

public record MtPlaceOrder(string OrderId);

public record MtOrderPlaced(string OrderId);

// --- MassTransit IConsumer<T> handler ---

public class PlaceOrderConsumer : IConsumer<MtPlaceOrder>
{
    public static string? LastOrderId;
    public static bool ReceivedContext;

    public static void Reset()
    {
        LastOrderId = null;
        ReceivedContext = false;
    }

    public async Task Consume(ConsumeContext<MtPlaceOrder> context)
    {
        LastOrderId = context.Message.OrderId;
        ReceivedContext = true;

        // Publish a cascading event via the ConsumeContext
        await context.Publish(new MtOrderPlaced(context.Message.OrderId));
    }
}

// --- Handler for the cascaded event ---

public class OrderPlacedCascadeHandler
{
    public static string? LastOrderId;

    public static void Reset()
    {
        LastOrderId = null;
    }

    public void Handle(MtOrderPlaced message)
    {
        LastOrderId = message.OrderId;
    }
}
