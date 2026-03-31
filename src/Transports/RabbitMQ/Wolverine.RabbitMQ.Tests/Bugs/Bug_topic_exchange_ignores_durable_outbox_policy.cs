using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_topic_exchange_ignores_durable_outbox_policy : IDisposable
{
    private readonly IHost _host;

    public Bug_topic_exchange_ignores_durable_outbox_policy()
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .AutoProvision();

                opts.StubAllExternalTransports();

                opts.UseRabbitMq().DeclareExchange("members", e =>
                {
                    e.ExchangeType = ExchangeType.Topic;
                    e.IsDurable = true;
                });

                opts.PublishMessagesToRabbitMqExchange<TopicExchangeBugMessage>("members",
                    _ => "member.created");

                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            })
            .Start();
    }

    [Fact]
    public void topic_exchange_endpoint_should_be_durable()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = runtime.Options.Transports
            .AllEndpoints()
            .OfType<RabbitMqExchange>()
            .FirstOrDefault(e => e.ExchangeName == "members");

        endpoint.ShouldNotBeNull();
        endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    public void Dispose()
    {
        _host?.Dispose();
    }
}

public class TopicExchangeBugMessage;

public static class TopicExchangeBugHandler
{
    public static void Handle(TopicExchangeBugMessage message)
    {
        // no-op
    }
}
