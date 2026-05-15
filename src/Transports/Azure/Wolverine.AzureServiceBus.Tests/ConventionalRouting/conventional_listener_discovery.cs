using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class conventional_listener_discovery : ConventionalRoutingContext
{
    [Fact]
    public async Task disable_sender_with_lambda()
    {
        await ConfigureConventions(c => c.QueueNameForSender(t =>
        {
            if (t == typeof(PublishedMessage))
            {
                return null; // should not be routed
            }

            return t.ToMessageTypeName().Replace('.', '-');
        }));

        await AssertNoRoutes<PublishedMessage>();
    }

    [Fact]
    public async Task exclude_types()
    {
        await ConfigureConventions(c => { c.ExcludeTypes(t => t == typeof(PublishedMessage)); });

        await AssertNoRoutes<PublishedMessage>();

        var uri = "sqs://published.message".ToUri();
        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        runtime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task include_types()
    {
        await ConfigureConventions(c => { c.IncludeTypes(t => t == typeof(PublishedMessage)); });

        await AssertNoRoutes<Message1>();

        (await PublishingRoutesFor<PublishedMessage>()).Any().ShouldBeTrue();

        var uri = "sqs://Message1".ToUri();
        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        runtime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task configure_sender_overrides()
    {
        await ConfigureConventions(c => c.ConfigureSending((c, _) => c.AddOutgoingRule(new FakeEnvelopeRule())));

        var route = (await PublishingRoutesFor<PublishedMessage>()).Single().As<MessageRoute>().Sender.Endpoint
            .ShouldBeOfType<AzureServiceBusQueue>();

        route.OutgoingRules.Single().ShouldBeOfType<FakeEnvelopeRule>();
    }

    [Fact]
    public async Task disable_listener_by_lambda()
    {
        await ConfigureConventions(c => c.QueueNameForListener(t =>
        {
            if (t == typeof(RoutedMessage))
            {
                return null; // should not be routed
            }

            return t.ToMessageTypeName().Replace('.', '-');
        }));

        var uri = "sqs://routed".ToUri();
        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(uri);

        // An endpoint may exist at this URI as a SENDER (since a handler is
        // registered for RoutedMessage and the framework eagerly pre-registers
        // sender configuration for handled message types — see GH-2588), but
        // the listener side must NOT have been created.
        if (endpoint != null) endpoint.IsListener.ShouldBeFalse();

        runtime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task configure_listener()
    {
        await ConfigureConventions(c => c.ConfigureListeners((x, _) =>
        {
            x.UseDurableInbox();
        }));

        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor("asb://queue/routed".ToUri())
            .ShouldBeOfType<AzureServiceBusQueue>();

        endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    public class FakeEnvelopeRule : IEnvelopeRule
    {
        public void Modify(Envelope envelope)
        {
            throw new NotImplementedException();
        }
    }
}
