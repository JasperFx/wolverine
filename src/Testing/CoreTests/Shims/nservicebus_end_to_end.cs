using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Handlers;
using Wolverine.Shims.NServiceBus;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Shims;

public class nservicebus_end_to_end : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public nservicebus_end_to_end(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseNServiceBusShims();

                opts.IncludeType<NsbSubmitOrderHandler>();
                opts.IncludeType<NsbOrderSubmittedCascadeHandler>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task handler_handles_message_and_publishes_cascading_event()
    {
        NsbSubmitOrderHandler.Reset();
        NsbOrderSubmittedCascadeHandler.Reset();

        var session = await _host.InvokeMessageAndWaitAsync(new NsbSubmitOrder("ORD-101"));

        NsbSubmitOrderHandler.LastOrderId.ShouldBe("ORD-101");
        NsbOrderSubmittedCascadeHandler.LastOrderId.ShouldBe("ORD-101");
    }

    [Fact]
    public async Task handler_receives_message_handler_context()
    {
        NsbSubmitOrderHandler.Reset();

        await _host.InvokeMessageAndWaitAsync(new NsbSubmitOrder("ORD-102"));

        NsbSubmitOrderHandler.LastOrderId.ShouldBe("ORD-102");
        NsbSubmitOrderHandler.ReceivedContext.ShouldBeTrue();
    }

    [Fact]
    public void print_generated_code_for_handler()
    {
        // Force code generation by resolving the handler
        _host.GetRuntime().Handlers.HandlerFor<NsbSubmitOrder>();

        var graph = _host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<NsbSubmitOrder>();
        chain.ShouldNotBeNull();

        _output.WriteLine("=== Generated Code for NsbSubmitOrder (NServiceBus IHandleMessages<T>) ===");
        _output.WriteLine(chain.SourceCode);
    }

    [Fact]
    public void print_generated_code_for_cascaded_handler()
    {
        // Force code generation by resolving the handler
        _host.GetRuntime().Handlers.HandlerFor<NsbOrderSubmitted>();

        var graph = _host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<NsbOrderSubmitted>();
        chain.ShouldNotBeNull();

        _output.WriteLine("=== Generated Code for NsbOrderSubmitted cascade handler ===");
        _output.WriteLine(chain.SourceCode);
    }
}

// --- NServiceBus shim message types ---

public record NsbSubmitOrder(string OrderId);

public record NsbOrderSubmitted(string OrderId);

// --- NServiceBus IHandleMessages<T> handler ---

public class NsbSubmitOrderHandler : IHandleMessages<NsbSubmitOrder>
{
    public static string? LastOrderId;
    public static bool ReceivedContext;

    public static void Reset()
    {
        LastOrderId = null;
        ReceivedContext = false;
    }

    public async Task Handle(NsbSubmitOrder message, IMessageHandlerContext context)
    {
        LastOrderId = message.OrderId;
        ReceivedContext = true;

        // Publish a cascading event via the IMessageHandlerContext
        await context.Publish(new NsbOrderSubmitted(message.OrderId));
    }
}

// --- Handler for the cascaded event ---

public class NsbOrderSubmittedCascadeHandler
{
    public static string? LastOrderId;

    public static void Reset()
    {
        LastOrderId = null;
    }

    public void Handle(NsbOrderSubmitted message)
    {
        LastOrderId = message.OrderId;
    }
}
