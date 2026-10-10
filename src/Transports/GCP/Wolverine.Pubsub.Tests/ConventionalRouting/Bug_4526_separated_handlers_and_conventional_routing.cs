using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

/// <summary>
/// GH-4526. Conventional routing under MultipleHandlerBehavior.Separated used to fail at start-up with
/// NotSupportedException on Pub/Sub. A topic fans out, so the shape is the one RabbitMQ and the Azure Service Bus
/// topic convention already use: the sender publishes to the message type's topic, and every handler Separated
/// moved out of the main chain listens on its own subscription to that topic, named from the handler type.
/// </summary>
public class Bug4526Fixture : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .UseConventionalRouting(x => x.IncludeTypes(t => t == typeof(SeparatedPubsubMessage)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // The two sticky handlers below are [WolverineIgnore]d so no other host in this assembly
                // discovers them: two sticky handlers for one message type change what a host provisions
                opts.Discovery.IncludeType<SeparatedPubsubConsumerOne>().IncludeType<SeparatedPubsubConsumerTwo>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class Bug_4526_separated_handlers_and_conventional_routing(Bug4526Fixture fixture, ITestOutputHelper output)
    : IClassFixture<Bug4526Fixture>
{
    private IHost _host => fixture.Host;

    private static string topicName(PubsubTransport transport)
        => transport.MaybeCorrectName(typeof(SeparatedPubsubMessage).ToMessageTypeName());

    private static Uri subscriptionUri(PubsubTransport transport, Type handlerType)
        => new($"pubsub://wolverine/{topicName(transport)}/{PubsubMessageRoutingConvention.SubscriptionNameForSeparatedHandler(transport, handlerType)}");

    [Fact]
    public void each_separated_handler_listens_on_its_own_subscription_to_the_message_types_topic()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();
        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();
        foreach (var uri in uris) output.WriteLine(uri.ToString());

        uris.ShouldContain(subscriptionUri(transport, typeof(SeparatedPubsubConsumerOne)));
        uris.ShouldContain(subscriptionUri(transport, typeof(SeparatedPubsubConsumerTwo)));

        // Both subscriptions sit on the ONE message type topic
        foreach (var subscription in transport.Subscriptions)
        {
            subscription.Server.Topic.Name.TopicId.ShouldBe(topicName(transport));
        }
    }

    [Fact]
    public void the_sender_publishes_to_the_single_topic()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();

        var destinations = runtime.RoutingFor(typeof(SeparatedPubsubMessage)).Routes
            .OfType<MessageRoute>().Select(x => x.Uri).ToArray();
        foreach (var uri in destinations) output.WriteLine(uri.ToString());

        // The topic fans out; the sender has exactly one destination
        destinations.ShouldBe([new Uri($"pubsub://wolverine/{topicName(transport)}")]);
    }

    [Fact]
    public void a_subscription_endpoint_is_found_by_its_own_uri_not_the_topics()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<PubsubTransport>();
        var uri = subscriptionUri(transport, typeof(SeparatedPubsubConsumerOne));

        // Before GH-4526 the lookup fell back to the topic name alone, which would have handed back the topic
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
        endpoint.ShouldNotBeSameAs(transport.GetOrCreateEndpoint(new Uri($"pubsub://wolverine/{topicName(transport)}")));
    }

    [Fact]
    public async Task every_separated_handler_receives_the_message()
    {
        var message = new SeparatedPubsubMessage(Guid.NewGuid());
        var tracked = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        foreach (var record in tracked.AllRecordsInOrder())
        {
            output.WriteLine(record.ToString());
        }

        // One copy per subscription, one subscription per handler
        tracked.Received.MessagesOf<SeparatedPubsubMessage>().Count().ShouldBe(2);
    }

    [Fact]
    public void an_invalid_name_says_what_the_rule_is()
    {
        var ex = Should.Throw<WolverinePubsubInvalidEndpointNameException>(() =>
            new PubsubEndpoint("not a valid name!", new PubsubTransport()));

        ex.Message.ShouldContain("not a valid name!");
        ex.Message.ShouldContain("must start with a letter");
        ex.Message.ShouldContain("goog");
    }
}

public record SeparatedPubsubMessage(Guid Id);

[WolverineIgnore]
[StickyHandler(nameof(SeparatedPubsubConsumerOne))]
public class SeparatedPubsubConsumerOne : IWolverineHandler
{
    public void Consume(SeparatedPubsubMessage message)
    {
    }
}

[WolverineIgnore]
[StickyHandler(nameof(SeparatedPubsubConsumerTwo))]
public class SeparatedPubsubConsumerTwo : IWolverineHandler
{
    public void Consume(SeparatedPubsubMessage message)
    {
    }
}
