using System.Text.Json.Serialization;
using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Transports.Local;

public class local_integration_specs : IntegrationContext
{
    public local_integration_specs(DefaultApp @default) : base(@default)
    {
    }

    private Task configure()
    {
        return with(opts =>
        {
            opts.Publish(x => x.Message<Message1>()
                .ToLocalQueue("incoming"));

            #region sample_opting_into_stj
            opts.UseSystemTextJsonForSerialization(stj =>
            {
                stj.UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode;
            });

            #endregion
        });
    }

    [Fact]
    public async Task send_a_message_and_get_the_response()
    {
        await configure();

        var message1 = new Message1();
        var session = await Host.SendMessageAndWaitAsync(message1, timeoutInMilliseconds: 15000);


        session.FindSingleTrackedMessageOfType<Message1>(MessageEventType.MessageSucceeded)
            .ShouldBeSameAs(message1);
    }

    [Fact]
    public async Task no_circuit_breaker()
    {
        await with(opts => { opts.LocalQueue("foo").UseDurableInbox(); });

        var runtime = Host.GetRuntime();
        var agent = runtime
            .Endpoints.GetOrBuildSendingAgent("local://foo".ToUri())
            .ShouldBeOfType<DurableLocalQueue>();

        agent.CircuitBreaker.ShouldBeNull();

        agent
            .Pipeline.ShouldBeOfType<HandlerPipeline>().ExecutorFactory.ShouldBeSameAs(runtime);
    }

    [Fact]
    public async Task build_isolated_watched_handler_pipeline_when_durable_and_circuit_breaker()
    {
        await with(opts =>
        {
            opts.LocalQueue("foo")
                .UseDurableInbox()
                .CircuitBreaker();
        });


        var runtime = Host.GetRuntime();
        var agent = runtime
            .Endpoints.GetOrBuildSendingAgent("local://foo".ToUri())
            .ShouldBeOfType<DurableLocalQueue>();

        var pipeline = agent
            .Pipeline.ShouldBeOfType<HandlerPipeline>();
        var circuitBreaker = pipeline.ExecutorFactory
            .ShouldBeOfType<CircuitBreakerTrackedExecutorFactory>();
        agent.CircuitBreaker.ShouldNotBeNull();
    }

    [Fact]
    public async Task individual_configuration_by_queue()
    {
        await with(opts =>
        {
            opts.LocalQueueFor<Message1>().MaximumParallelMessages(6);
        });

        var runtime = Host.GetRuntime();
        var queue = runtime.Options.LocalRouting.FindQueueForMessageType(typeof(Message1));
        queue
            .MaxDegreeOfParallelism.ShouldBe(6);
    }

    [Fact]
    public async Task individual_configuration_by_queue_2()
    {
        await with(opts =>
        {
            opts.LocalQueueFor<Message1>().MaximumParallelMessages(6);
        });

        var runtime = Host.GetRuntime();
        var queue = runtime.Options.LocalRouting.FindQueueForMessageType(typeof(Message1));
        queue
            .MaxDegreeOfParallelism.ShouldBe(6);
    }

    [Fact]
    public async Task not_allowed_to_use_strict_ordering()
    {
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await with(o => o.LocalQueue("one").ListenWithStrictOrdering());
        });
    }
}