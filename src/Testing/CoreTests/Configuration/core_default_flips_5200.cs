using CoreTests.Acceptance;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5200. Three core defaults Wolverine 7.0 turns on: agent command compression, GroupByGroupId() for a batched
/// message type in a partitioned topology, and the in-memory idempotency guard on NativeAck endpoints. Each one in
/// its new state, each opt-out, and RestoreV6Defaults() putting the 6.x behavior back. The migration guide's 7.0
/// defaults table names this class for the last two; CompressAgentCommands is in restore_v6_defaults_tests.
/// </summary>
public class core_default_flips_5200
{
    // ---------------------------------------------------------------- 1. CompressAgentCommands

    [Fact]
    public void agent_commands_are_compressed_by_default()
    {
        new WolverineOptions().Durability.CompressAgentCommands.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_false_turns_compression_off_whichever_order(bool restoreFirst)
    {
        var options = new WolverineOptions();
        if (restoreFirst) options.RestoreV6Defaults();
        options.Durability.CompressAgentCommands = false;
        if (!restoreFirst) options.RestoreV6Defaults();

        options.Durability.CompressAgentCommands.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_true_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        var options = new WolverineOptions();
        if (restoreFirst) options.RestoreV6Defaults();
        options.Durability.CompressAgentCommands = true;
        if (!restoreFirst) options.RestoreV6Defaults();

        options.Durability.CompressAgentCommands.ShouldBeTrue();
    }

    /// <summary>
    /// The 6.44 floor. Reading the compressed content type does not depend on CompressAgentCommands, so a node
    /// that does not write it (here one with the 6.x defaults restored, standing in for a 6.44+ node) still reads
    /// what a 7.0 node writes. A node older than 6.44 has no serializer for the content type at all, which is why
    /// every node must be on 6.44 or later before the first 7.0 node starts.
    /// </summary>
    [Fact]
    public async Task a_node_that_does_not_write_compressed_commands_still_reads_them()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.RestoreV6Defaults())
            .StartAsync(TestContext.Current.CancellationToken);

        var options = host.GetRuntime().Options;
        options.Durability.CompressAgentCommands.ShouldBeFalse();

        options.TryFindSerializer(CompressedIntrinsicSerializer.MimeType)
            .ShouldBeSameAs(CompressedIntrinsicSerializer.Instance);
    }

    // ---------------------------------------------------------------- 2. GroupByGroupId() for partitioned batches

    private static async Task<IHost> batchingHost(Action<WolverineOptions> configure, bool withTopology = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MessagePartitioning.ByMessage<IPartitionedOrderMessage>(x => x.OrderId);

                if (withTopology)
                {
                    opts.MessagePartitioning.PublishToPartitionedLocalMessaging("gh5200batch", 3,
                        topology => { topology.MessagesImplementing<IPartitionedOrderMessage>(); });
                }

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static BatchingOptions batchingFor(IHost host)
    {
        return host.GetRuntime().Options.BatchDefinitions.Single(x => x.ElementType == typeof(OrderTouched));
    }

    [Fact]
    public async Task a_partitioned_batch_on_a_dedicated_queue_is_grouped_by_group_id()
    {
        // 6.x left this batch on the tenant-only batcher: no group id on the batch envelope
        using var host = await batchingHost(opts => opts.BatchMessagesOf<OrderTouched>(b => b.ExecuteOnDedicatedLocalQueue()));

        var batching = batchingFor(host);
        batching.ExecutionSlots.ShouldBeNull();
        batching.Batcher.ShouldBeOfType<GroupIdMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task a_partitioned_batch_on_a_named_queue_is_grouped_by_group_id()
    {
        using var host = await batchingHost(opts => opts.BatchMessagesOf<OrderTouched>(b => b.LocalExecutionQueueName = "gh5200-named"));

        batchingFor(host).Batcher.ShouldBeOfType<GroupIdMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task DoNotGroupByGroupId_keeps_the_tenant_only_batcher()
    {
        using var host = await batchingHost(opts => opts.BatchMessagesOf<OrderTouched>(b =>
        {
            b.ExecuteOnDedicatedLocalQueue();
            b.DoNotGroupByGroupId();
        }));

        batchingFor(host).Batcher.ShouldBeOfType<DefaultMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task RestoreV6Defaults_keeps_the_tenant_only_batcher()
    {
        using var host = await batchingHost(opts =>
        {
            opts.RestoreV6Defaults();
            opts.BatchMessagesOf<OrderTouched>(b => b.ExecuteOnDedicatedLocalQueue());
        });

        batchingFor(host).Batcher.ShouldBeOfType<DefaultMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task a_batch_on_the_topology_slots_is_still_grouped_after_RestoreV6Defaults()
    {
        // The 6.25 behavior (GH-3867): a batch can only be placed on a slot if it belongs to one group
        using var host = await batchingHost(opts =>
        {
            opts.RestoreV6Defaults();
            opts.BatchMessagesOf<OrderTouched>();
        });

        var batching = batchingFor(host);
        batching.ExecutionSlots.ShouldNotBeNull();
        batching.Batcher.ShouldBeOfType<GroupIdMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task an_application_supplied_batcher_is_left_alone()
    {
        using var host = await batchingHost(opts => opts.BatchMessagesOf<OrderTouched>(b =>
        {
            b.ExecuteOnDedicatedLocalQueue();
            b.Batcher = new PassthroughOrderBatcher();
        }));

        batchingFor(host).Batcher.ShouldBeOfType<PassthroughOrderBatcher>();
    }

    [Fact]
    public async Task batching_outside_a_partitioned_topology_is_unchanged()
    {
        using var host = await batchingHost(opts => opts.BatchMessagesOf<OrderTouched>(), withTopology: false);

        batchingFor(host).Batcher.ShouldBeOfType<DefaultMessageBatcher<OrderTouched>>();
    }

    // ---------------------------------------------------------------- 3. in-memory idempotency on NativeAck

    private class NativeAckCapableEndpoint : StubEndpoint
    {
        public NativeAckCapableEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
        {
        }

        protected override bool supportsNativeAck => true;
    }

    private static Endpoint compiledListener(Action<WolverineOptions> configureOptions,
        Action<IListenerConfiguration> configureListener)
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(configureOptions).Build();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = new NativeAckCapableEndpoint("gh5200", new StubTransport());
        configureListener(new ListenerConfiguration(endpoint));
        endpoint.Compile(runtime);

        return endpoint;
    }

    [Fact]
    public void a_native_ack_listener_stays_opt_in_in_7_0()
    {
        // GH-5200 turned this on by default; TestAllTransports showed the guard acknowledges an in-flight redelivery,
        // which loses the message on Pulsar when the node drains. It stays opt-in until GH-5533.
        var endpoint = compiledListener(_ => { }, x => x.ProcessInParallelWithNativeAcks());

        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
        endpoint.InMemoryIdempotency.ShouldBeNull();
        endpoint.IdempotencyGuard.ShouldBeNull();
    }

    [Fact]
    public void explicit_tuning_on_a_native_ack_listener_wins()
    {
        var endpoint = compiledListener(_ => { },
            x => x.ProcessInParallelWithNativeAcks().WithInMemoryIdempotency(30.Seconds(), 5000));

        endpoint.InMemoryIdempotency!.Window.ShouldBe(30.Seconds());
        endpoint.InMemoryIdempotency.MaxTracked.ShouldBe(5000);
    }

    [Fact]
    public void WithoutInMemoryIdempotency_opts_a_native_ack_listener_out()
    {
        var endpoint = compiledListener(_ => { },
            x => x.ProcessInParallelWithNativeAcks().WithoutInMemoryIdempotency());

        endpoint.InMemoryIdempotency.ShouldBeNull();
        endpoint.IdempotencyGuard.ShouldBeNull();
    }

    [Fact]
    public void an_all_listeners_policy_opts_native_ack_listeners_out()
    {
        // The opt-out for listeners the application never holds, such as a native-ack topology's slot listeners
        using var host = Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            // IsListener set eagerly, as ListenForMessagesFrom() and the transports' ListenTo...() calls do
            opts.Transports.GetOrCreate<StubTransport>().Endpoints["gh5200-policy"] =
                new NativeAckCapableEndpoint("gh5200-policy", opts.Transports.GetOrCreate<StubTransport>())
                    { IsListener = true };
            new ListenerConfiguration(opts.Transports.GetOrCreate<StubTransport>().Endpoints["gh5200-policy"])
                .ProcessInParallelWithNativeAcks();

            opts.Policies.AllListeners(x => x.WithoutInMemoryIdempotency());
        }).Build();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = runtime.Options.Transports.GetOrCreate<StubTransport>().Endpoints["gh5200-policy"];
        endpoint.Compile(runtime);

        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
        endpoint.InMemoryIdempotency.ShouldBeNull();
        endpoint.IdempotencyGuard.ShouldBeNull();
    }

    [Fact]
    public void RestoreV6Defaults_leaves_a_native_ack_listener_without_the_guard()
    {
        var endpoint = compiledListener(opts => opts.RestoreV6Defaults(), x => x.ProcessInParallelWithNativeAcks());

        endpoint.InMemoryIdempotency.ShouldBeNull();
        endpoint.IdempotencyGuard.ShouldBeNull();
    }

    [Fact]
    public void an_explicit_opt_in_wins_over_RestoreV6Defaults()
    {
        var endpoint = compiledListener(opts => opts.RestoreV6Defaults(),
            x => x.ProcessInParallelWithNativeAcks().WithInMemoryIdempotency());

        endpoint.IdempotencyGuard.ShouldNotBeNull();
    }

    [Fact]
    public void other_listener_modes_stay_opt_in()
    {
        compiledListener(_ => { }, x => x.BufferedInMemory()).InMemoryIdempotency.ShouldBeNull();
        compiledListener(_ => { }, x => x.ProcessInline()).InMemoryIdempotency.ShouldBeNull();

        // Durable gets nothing built and no warning, since the application never asked for the guard
        var durable = compiledListener(_ => { }, x => x.UseDurableInbox());
        durable.InMemoryIdempotency.ShouldBeNull();
        ListenerConfigurationValidator.Validate(durable).ShouldBeEmpty();
    }
}
