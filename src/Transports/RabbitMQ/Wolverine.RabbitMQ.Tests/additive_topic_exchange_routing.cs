using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// The RabbitMQ twin of GH-4930. PublishMessagesToRabbitMqExchange registers the same TopicRouting source as
/// the NATS dynamic subjects, so an explicit local queue rule for the same message type was never consulted.
/// The additive: true overload lets every other routing rule apply as well; the original overload is unchanged.
/// </summary>
public class additive_topic_exchange_routing
{
    private readonly ITestOutputHelper _output;

    public additive_topic_exchange_routing(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task publishes_to_the_exchange_and_to_the_local_queue()
    {
        var exchangeName = $"additive-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(AdditiveRabbitCommandHandler));

                #region sample_publish_to_rabbitmq_exchange_and_local_queue

                opts.UseRabbitMq().AutoProvision();

                // Every AdditiveRabbitCommand goes to the topic exchange with a topic computed from the message...
                opts.PublishMessagesToRabbitMqExchange<AdditiveRabbitCommand>(exchangeName,
                    m => $"commands.{m.Region}", additive: true);

                // ...and because that routing is additive, to the local "commands" queue at the same time
                opts.PublishMessage<AdditiveRabbitCommand>().ToLocalQueue("commands");

                #endregion
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        _output.WriteLine(runtime.ExplainRoutingFor(typeof(AdditiveRabbitCommand)).ToText());

        var routes = runtime.RoutingFor(typeof(AdditiveRabbitCommand)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ToArray();

        routes.ShouldContain(x => x.StartsWith("local://commands"));
        routes.ShouldContain(x => x.StartsWith("rabbitmq://exchange/" + exchangeName));

        // Nothing in this test is bound to the exchange, so the session waits on the local handler only
        var session = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .PublishMessageAndWaitAsync(new AdditiveRabbitCommand("west"));

        session.Executed.SingleMessage<AdditiveRabbitCommand>().Region.ShouldBe("west");
        session.Sent.Envelopes().ShouldContain(x => x.Destination!.Scheme == "rabbitmq" && x.TopicName == "commands.west");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_original_overload_still_routes_only_to_the_exchange()
    {
        var exchangeName = $"not-additive-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(AdditiveRabbitCommandHandler));
                opts.UseRabbitMq().AutoProvision();

                opts.PublishMessagesToRabbitMqExchange<AdditiveRabbitCommand>(exchangeName, m => $"commands.{m.Region}");
                opts.PublishMessage<AdditiveRabbitCommand>().ToLocalQueue("commands");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        runtime.RoutingFor(typeof(AdditiveRabbitCommand)).Routes
            .Select(x => x.Describe().Endpoint.ToString())
            .ShouldHaveSingleItem()
            .ShouldStartWith("rabbitmq://exchange/" + exchangeName);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record AdditiveRabbitCommand(string Region);

[WolverineIgnore]
public static class AdditiveRabbitCommandHandler
{
    public static void Handle(AdditiveRabbitCommand command)
    {
    }
}
