using JasperFx.Events.EventModeling;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// GH-3989 acceptance: a host with ListenToRabbitQueue("stripe-events").ExternalSystem("Stripe") reports
// the name on that endpoint's descriptor, and the merged EventModelDescriptor carries an external-system
// element named "Stripe" attached to the slice whose trigger is that endpoint.
public class external_system_3989 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "external-system-3989";
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StripeChargeSucceededHandler))
                    .IncludeType(typeof(RefundOrderHandler));

                opts.ListenToRabbitQueue("stripe-events")
                    .ExternalSystem("Stripe")
                    .DefaultIncomingMessage<StripeChargeSucceeded>();

                opts.PublishMessage<IssueStripeRefund>().ToRabbitQueue("stripe-refunds").ExternalSystem("Stripe");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_rabbit_endpoints_report_the_external_system()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);

        capabilities.MessagingEndpoints.Single(x => x.Uri == new Uri("rabbitmq://queue/stripe-events")).ExternalSystem.ShouldBe("Stripe");
        capabilities.MessagingEndpoints.Single(x => x.Uri == new Uri("rabbitmq://queue/stripe-refunds")).ExternalSystem.ShouldBe("Stripe");
    }

    [Fact]
    public async Task the_merged_event_model_attaches_stripe_to_the_slices_the_queues_trigger_and_feed()
    {
        var model = await WolverineEventModelExport.AssembleAsync(_host.Services, token: TestContext.Current.CancellationToken);

        var inbound = model.Slices.Single(x => x.Name == nameof(StripeChargeSucceeded));
        var trigger = inbound.ExternalSystems.ShouldHaveSingleItem();
        trigger.Name.ShouldBe("Stripe");
        trigger.Direction.ShouldBe(ExternalSystemDirection.Inbound);
        trigger.EndpointUri.ShouldBe("rabbitmq://queue/stripe-events");
        inbound.Pattern.ShouldBe(SlicePattern.Translation);
        inbound.TriggerKind.ShouldBe(TriggerKind.External);
        inbound.Elements.ShouldContain(x => x.Kind == EventModelElementKind.ExternalSystem && x.Label == "Stripe");

        var outbound = model.Slices.Single(x => x.Name == nameof(RefundOrder));
        var target = outbound.ExternalSystems.ShouldHaveSingleItem();
        target.Name.ShouldBe("Stripe");
        target.Direction.ShouldBe(ExternalSystemDirection.Outbound);
        target.EndpointUri.ShouldBe("rabbitmq://queue/stripe-refunds");
    }
}

public record StripeChargeSucceeded(string ChargeId);
public record RecordPayment(string ChargeId);
public record RefundOrder(string OrderId);
public record IssueStripeRefund(string OrderId);

public class StripeChargeSucceededHandler
{
    public static RecordPayment Handle(StripeChargeSucceeded charge) => new(charge.ChargeId);
}

public class RefundOrderHandler
{
    public static IssueStripeRefund Handle(RefundOrder command) => new(command.OrderId);
}
