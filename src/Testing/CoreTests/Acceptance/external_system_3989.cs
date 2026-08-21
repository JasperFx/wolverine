using JasperFx.Events.EventModeling;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance.ExternalSystem3989;

// GH-3989: the *edge* of a translation slice is derived — a listening or sending endpoint to something
// outside the application IS the external-system boundary — but the *name* ("Stripe") is the one thing
// code cannot say, so it is declared on the endpoint with .ExternalSystem("Stripe"), never in the event
// model overlay. It flows out through EndpointDescriptor and lands on the slice as an external-system
// element. TCP endpoints here so no broker is needed; the RabbitMQ-flavoured twin lives in
// Wolverine.RabbitMQ.Tests.
public class external_system_3989 : IAsyncLifetime
{
    private IHost _host = null!;
    private int _stripeInboundPort;
    private int _stripeOutboundPort;
    private int _erpInboundPort;
    private int _ledgerPort;

    public async ValueTask InitializeAsync()
    {
        _stripeInboundPort = PortFinder.GetAvailablePort();
        _stripeOutboundPort = PortFinder.GetAvailablePort();
        _erpInboundPort = PortFinder.GetAvailablePort();
        _ledgerPort = PortFinder.GetAvailablePort();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "external-system-3989";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StripeChargeSucceededHandler))
                    .IncludeType(typeof(RefundOrderHandler))
                    .IncludeType(typeof(PlaceOrderHandler));

                // inbound: Stripe pushes charge events onto this listener; DefaultIncomingMessage<T> is what
                // binds the listener to the slice (the other binding is a handler stuck to the listener). The
                // TCP helper returns the interface, so reach the generic base for DefaultIncomingMessage<T>;
                // the broker configurations (RabbitMqListenerConfiguration etc.) expose it directly.
                ((ListenerConfiguration)opts.ListenAtPort(_stripeInboundPort))
                    .DefaultIncomingMessage<StripeChargeSucceeded>()
                    .ExternalSystem("Stripe");

                // inbound, bound to nothing: still a boundary to render
                opts.ListenAtPort(_erpInboundPort).ExternalSystem("Legacy ERP").Named("erp-feed");

                // outbound: refund requests go to Stripe
                opts.PublishMessage<IssueStripeRefund>().ToPort(_stripeOutboundPort).ExternalSystem("Stripe");

                // outbound: a plain internal subscriber, no external system
                opts.PublishMessage<OrderPlacedNotification>().ToPort(_ledgerPort);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void the_endpoint_descriptor_reports_the_external_system_name()
    {
        var endpoint = _host.GetRuntime().Options.Transports.AllEndpoints()
            .Single(x => x.Uri.Port == _stripeInboundPort);

        endpoint.ExternalSystemName.ShouldBe("Stripe");
        new EndpointDescriptor(endpoint).ExternalSystem.ShouldBe("Stripe");

        var ledger = _host.GetRuntime().Options.Transports.AllEndpoints().Single(x => x.Uri.Port == _ledgerPort);
        new EndpointDescriptor(ledger).ExternalSystem.ShouldBeNull();
    }

    [Fact]
    public async Task the_capabilities_snapshot_carries_the_name_on_the_messaging_endpoint()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);

        capabilities.MessagingEndpoints.Single(x => x.Uri.Port == _stripeInboundPort).ExternalSystem.ShouldBe("Stripe");
        capabilities.MessagingEndpoints.Single(x => x.Uri.Port == _stripeOutboundPort).ExternalSystem.ShouldBe("Stripe");
        capabilities.MessagingEndpoints.Single(x => x.Uri.Port == _ledgerPort).ExternalSystem.ShouldBeNull();
    }

    [Fact]
    public void an_inbound_listener_is_the_trigger_of_the_slice_for_its_message_type()
    {
        var model = WolverineEventModelSource.Describe(_host.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == nameof(StripeChargeSucceeded));

        var system = slice.ExternalSystems.ShouldHaveSingleItem();
        system.Name.ShouldBe("Stripe");
        system.Direction.ShouldBe(ExternalSystemDirection.Inbound);
        system.EndpointUri.ShouldBe($"tcp://localhost:{_stripeInboundPort}/");

        slice.Pattern.ShouldBe(SlicePattern.Translation);
        slice.TriggerKind.ShouldBe(TriggerKind.External);
        slice.TriggerLabel.ShouldNotBeNull();

        // and the handler roles are untouched
        slice.CommandType!.Name.ShouldBe(nameof(StripeChargeSucceeded));
        slice.HandlerType!.Name.ShouldBe(nameof(StripeChargeSucceededHandler));
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(RecordPayment) });

        // the external system renders as an element on the wireframe lane
        slice.Elements.ShouldContain(x => x.Kind == EventModelElementKind.ExternalSystem && x.Label == "Stripe");
    }

    [Fact]
    public void an_outbound_subscriber_puts_the_external_system_on_the_publishing_slice()
    {
        var model = WolverineEventModelSource.Describe(_host.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == nameof(RefundOrder));

        var system = slice.ExternalSystems.ShouldHaveSingleItem();
        system.Name.ShouldBe("Stripe");
        system.Direction.ShouldBe(ExternalSystemDirection.Outbound);
        system.EndpointUri.ShouldBe($"tcp://localhost:{_stripeOutboundPort}/");

        // a pure relay — no aggregate, no events of its own — is a translation slice
        slice.Pattern.ShouldBe(SlicePattern.Translation);

        // an internal subscriber is not an external system
        model.Slices.Single(x => x.Name == nameof(PlaceOrder)).ExternalSystems.ShouldBeEmpty();
        model.Slices.Single(x => x.Name == nameof(PlaceOrder)).Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void a_named_listener_bound_to_no_slice_still_renders_as_a_boundary()
    {
        var model = WolverineEventModelSource.Describe(_host.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == "erp-feed");

        slice.Pattern.ShouldBe(SlicePattern.Translation);
        slice.TriggerKind.ShouldBe(TriggerKind.External);
        slice.CommandType.ShouldBeNull();
        var system = slice.ExternalSystems.ShouldHaveSingleItem();
        system.Name.ShouldBe("Legacy ERP");
        system.Direction.ShouldBe(ExternalSystemDirection.Inbound);
    }

    [Fact]
    public async Task the_external_systems_survive_assembly_and_the_wire()
    {
        var assembled = await WolverineEventModelExport.AssembleAsync(_host.Services, token: TestContext.Current.CancellationToken);
        assembled.Slices.Single(x => x.Name == nameof(StripeChargeSucceeded)).ExternalSystems.Single().Name.ShouldBe("Stripe");

        var json = WolverineEventModelExport.ToJson(assembled);
        json.ShouldContain("\"externalSystems\"");
        json.ShouldContain("\"direction\": \"Inbound\"");
        json.ShouldContain("\"direction\": \"Outbound\"");
        json.ShouldContain("\"pattern\": \"Translation\"");

        var back = WolverineEventModelExport.FromJson(json)!;
        var slice = back.Slices.Single(x => x.Name == nameof(StripeChargeSucceeded));
        var system = slice.ExternalSystems.ShouldHaveSingleItem();
        system.Name.ShouldBe("Stripe");
        system.Direction.ShouldBe(ExternalSystemDirection.Inbound);
        system.EndpointUri.ShouldBe($"tcp://localhost:{_stripeInboundPort}/");
        slice.Pattern.ShouldBe(SlicePattern.Translation);
        slice.TriggerKind.ShouldBe(TriggerKind.External);
    }
}

public record StripeChargeSucceeded(string ChargeId);
public record RecordPayment(string ChargeId);
public record RefundOrder(string OrderId);
public record IssueStripeRefund(string OrderId);
public record PlaceOrder(string OrderId);
public record OrderPlacedNotification(string OrderId);

public class StripeChargeSucceededHandler
{
    public static RecordPayment Handle(StripeChargeSucceeded charge) => new(charge.ChargeId);
}

public class RefundOrderHandler
{
    public static IssueStripeRefund Handle(RefundOrder command) => new(command.OrderId);
}

public class PlaceOrderHandler
{
    public static OrderPlacedNotification Handle(PlaceOrder command) => new(command.OrderId);
}
