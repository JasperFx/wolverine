using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3712. Listener settings that a given EndpointMode simply ignores used to be accepted in silence.
/// </summary>
public class listener_configuration_validation
{
    private static Endpoint compiledEndpoint(Action<WolverineOptions> configure, string uri = "stub://one")
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(configure).Build();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = options.Transports.AllEndpoints().Single(x => x.Uri == new Uri(uri));
        endpoint.Compile(runtime);

        return endpoint;
    }

    [Fact]
    public void inline_plus_partitioned_processing_is_fatal()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://one")
                .ProcessInline()
                .PartitionProcessingByGroupId(PartitionSlots.Five);
        });

        var problem = ListenerConfigurationValidator.Validate(endpoint).Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
        problem.Message.ShouldContain("PartitionProcessingByGroupId()");
        problem.Message.ShouldContain("stub://one");
        // GH-3708: the message now points at the mode that exists for exactly this combination
        problem.Message.ShouldContain("ProcessInParallelWithNativeAcks()");
        problem.Message.ShouldContain("BufferedInMemory()");
    }

    [Fact]
    public async Task inline_plus_partitioned_processing_stops_the_host_from_starting()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.ListenForMessagesFrom("stub://partitioned-inline")
                    .ProcessInline()
                    .PartitionProcessingByGroupId(PartitionSlots.Nine);
            }).StartAsync();
        });

        ex.Message.ShouldContain("stub://partitioned-inline");
        ex.Message.ShouldContain("PartitionProcessingByGroupId()");
    }

    [Fact]
    public void inline_plus_explicit_parallelism_only_warns()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://one")
                .ProcessInline()
                .MaximumParallelMessages(20);
        });

        var problem = ListenerConfigurationValidator.Validate(endpoint).Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
        problem.Message.ShouldContain("maximum parallelism of 20");
        problem.Message.ShouldContain("MaximumParallelMessages()");
    }

    [Fact]
    public void inline_plus_exclusive_node_with_parallelism_only_warns()
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(_ => { }).Build();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // ExclusiveNodeWithParallelism() is only on the concrete ListenerConfiguration
        var endpoint = new StubEndpoint("exclusive-inline", new StubTransport());
        var config = new ListenerConfiguration(endpoint);
        config.ExclusiveNodeWithParallelism(20);
        config.ProcessInline();
        ((IDelayedEndpointConfiguration)config).Apply();

        endpoint.Compile(runtime);

        var problem = ListenerConfigurationValidator.Validate(endpoint).Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
        problem.Message.ShouldContain("maximum parallelism of 20");

        // The exclusivity half of that method still applies
        endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        endpoint.MaxDegreeOfParallelism.ShouldBe(1);
    }

    [Fact]
    public void inline_plus_buffering_limits_only_warns()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://one")
                .BufferedInMemory(new BufferingLimits(250, 100))
                .ProcessInline();
        });

        var problem = ListenerConfigurationValidator.Validate(endpoint).Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
        problem.Message.ShouldContain("BufferingLimits");
    }

    [Fact]
    public void plain_inline_endpoint_has_no_problems()
    {
        var endpoint = compiledEndpoint(opts => { opts.ListenForMessagesFrom("stub://one").ProcessInline(); });

        ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();

        // ...and the default parallelism is not reported as though it were live
        endpoint.MaxDegreeOfParallelism.ShouldBe(1);
        endpoint.DescribeMaxDegreeOfParallelism().ShouldBe("n/a (Inline)");
    }

    [Fact]
    public void buffered_plus_partitioned_processing_is_still_valid()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://one")
                .BufferedInMemory()
                .MaximumParallelMessages(10)
                .PartitionProcessingByGroupId(PartitionSlots.Five);
        });

        ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();
        endpoint.MaxDegreeOfParallelism.ShouldBe(10);
        endpoint.DescribeMaxDegreeOfParallelism().ShouldBe("10");
    }

    [Fact]
    public void durable_plus_partitioned_processing_is_still_valid()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://one")
                .UseDurableInbox(new BufferingLimits(250, 100))
                .PartitionProcessingByGroupId(PartitionSlots.Three);
        });

        ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();
    }

    [Fact]
    public void a_send_only_endpoint_is_not_validated_as_a_listener()
    {
        var endpoint = compiledEndpoint(opts => opts.PublishAllMessages().To("stub://sender"), "stub://sender");

        endpoint.Mode = EndpointMode.Inline;
        endpoint.MaxDegreeOfParallelism = 20;

        endpoint.IsListener.ShouldBeFalse();
        ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();
    }

    // GH-4022. A local queue can only be BufferedInMemory or Durable -- it has no transport
    // listener for Inline to execute a message on.
    [Fact]
    public void process_inline_on_a_local_queue_throws_eagerly()
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(_ => { }).Build();
        var options = host.Services.GetRequiredService<WolverineOptions>();

        // Used to be accepted here, then throw a bare, message-less NotSupportedException out of
        // LocalQueue.BuildAgent() the first time anything sent to the queue.
        var ex = Should.Throw<NotSupportedException>(() => options.LocalQueue("inline-local").ProcessInline());

        ex.Message.ShouldContain("ProcessInline");
        ex.Message.ShouldContain("BufferedInMemory");
        ex.Message.ShouldContain("UseDurableInbox");
    }

    [Fact]
    public void a_local_queue_in_inline_mode_is_fatal()
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(_ => { }).Build();
        var options = host.Services.GetRequiredService<WolverineOptions>();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // Assigning the mode directly gets around the eager guard on ProcessInline()
        var queue = options.Transports.GetOrCreate<LocalTransport>().QueueFor("inline-local");
        queue.Mode = EndpointMode.Inline;
        queue.Compile(runtime);

        var problem = ListenerConfigurationValidator.Validate(queue).Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
        problem.Message.ShouldContain("ProcessInline()");
        problem.Message.ShouldContain("local://inline-local");
    }

    [Fact]
    public async Task a_lazily_configured_inline_local_queue_stops_the_host_from_starting()
    {
        // LocalQueueFor<T>() resolves its queue lazily, so ProcessInline()'s eager guard never sees a
        // LocalQueue and the bootstrap validation is the only thing standing between this configuration
        // and a message-less NotSupportedException at the first send.
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.LocalQueueFor<InlineLocalQueueMessage>().ProcessInline();
            }).StartAsync();
        });

        ex.Message.ShouldContain("ProcessInline()");
        ex.Message.ShouldContain("local queue");
    }

    [Fact]
    public void a_normal_local_queue_has_no_problems()
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(_ => { }).Build();
        var options = host.Services.GetRequiredService<WolverineOptions>();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var queue = options.Transports.GetOrCreate<LocalTransport>().QueueFor("buffered-local");
        queue.Compile(runtime);

        queue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        ListenerConfigurationValidator.Validate(queue).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void process_inline_and_maximum_parallel_messages_converge_regardless_of_call_order(bool inlineFirst)
    {
        var endpoint = compiledEndpoint(opts =>
        {
            var config = opts.ListenForMessagesFrom("stub://one");

            if (inlineFirst)
            {
                config.ProcessInline().MaximumParallelMessages(20);
            }
            else
            {
                config.MaximumParallelMessages(20).ProcessInline();
            }
        });

        endpoint.Mode.ShouldBe(EndpointMode.Inline);
        endpoint.MaxDegreeOfParallelism.ShouldBe(1);
        endpoint.DiscardedMaxDegreeOfParallelism.ShouldBe(20);

        // Same two calls, same warning, either way around
        ListenerConfigurationValidator.Validate(endpoint).Single()
            .Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
    }
}

public record InlineLocalQueueMessage(string Name);
