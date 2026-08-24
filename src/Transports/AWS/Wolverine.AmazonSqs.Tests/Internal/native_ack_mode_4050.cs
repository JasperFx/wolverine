using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
///     GH-4050. The configuration-time half of the SQS native ack adoption: which endpoints accept the mode, what
///     the receive batch size defaults to under it, and the FIFO verdict.
/// </summary>
public class native_ack_mode_4050
{
    private static AmazonSqsQueue queue(string name = "native-ack-defaults")
    {
        return new AmazonSqsQueue(name, new AmazonSqsTransport());
    }

    [Fact]
    public void sqs_queues_opt_into_native_acks()
    {
        queue().SupportsMode(EndpointMode.NativeAck).ShouldBeTrue();
    }

    [Fact]
    public void the_mode_can_actually_be_assigned()
    {
        var endpoint = queue();
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
    }

    /// <summary>
    ///     The mode's back pressure is the broker's delivery window, not a BackPressureAgent counting queued
    ///     messages -- which for SQS means the receive batch size below is the only lever there is.
    /// </summary>
    [Fact]
    public void native_ack_endpoints_do_not_enforce_in_process_back_pressure()
    {
        var endpoint = queue();
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public void an_unsettled_sqs_delivery_is_on_a_clock()
    {
        // The premise of the whole lease contract; a regression here would silently stop ListeningAgent from
        // demanding renewal on a native ack SQS listener.
        queue().holdsExpiringLease.ShouldBeTrue();
    }

    #region receive batch size defaults

    [Fact]
    public void receive_batch_size_default_is_the_sqs_maximum_for_every_other_mode()
    {
        foreach (var mode in new[] { EndpointMode.BufferedInMemory, EndpointMode.Durable, EndpointMode.Inline })
        {
            var endpoint = queue();
            endpoint.MaxDegreeOfParallelism = 1;
            endpoint.Mode = mode;

            // Those modes settle the whole batch before any handler runs, so a full batch costs nothing
            endpoint.MaxNumberOfMessages.ShouldBe(AmazonSqsQueue.MaximumReceiveBatchSize);
        }
    }

    [Fact]
    public void native_ack_receive_batch_size_covers_twice_the_parallel_lanes()
    {
        var endpoint = queue();
        endpoint.MaxDegreeOfParallelism = 3;
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.MaxNumberOfMessages.ShouldBe(6);
    }

    /// <summary>
    ///     The case the arm exists for. A sequential native ack endpoint that received ten at a time would hold
    ///     nine unsettled deliveries -- nine visibility timeouts to renew, nine redeliveries on a crash -- through
    ///     nine handler durations it gains no throughput from.
    /// </summary>
    [Fact]
    public void a_sequential_native_ack_endpoint_does_not_drag_in_nine_messages_it_cannot_run()
    {
        var endpoint = queue();
        endpoint.MaxDegreeOfParallelism = 1;
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.MaxNumberOfMessages.ShouldBe(2);
    }

    [Fact]
    public void native_ack_receive_batch_size_is_capped_at_the_sqs_maximum()
    {
        var endpoint = queue();
        endpoint.MaxDegreeOfParallelism = 20;
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.MaxNumberOfMessages.ShouldBe(AmazonSqsQueue.MaximumReceiveBatchSize);
    }

    /// <summary>
    ///     A group-partitioned endpoint's busy lane count is its slot count, which is independent of
    ///     MaxDegreeOfParallelism -- the slot count is what has to be covered.
    /// </summary>
    [Fact]
    public void native_ack_receive_batch_size_covers_partition_slots_when_group_partitioned()
    {
        var endpoint = queue();
        endpoint.MaxDegreeOfParallelism = 1;
        endpoint.GroupShardingSlotNumber = PartitionSlots.Five;
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.MaxNumberOfMessages.ShouldBe(AmazonSqsQueue.MaximumReceiveBatchSize);
    }

    [Fact]
    public void an_explicit_receive_batch_size_always_wins()
    {
        var endpoint = queue();
        endpoint.MaxDegreeOfParallelism = 1;
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.MaxNumberOfMessages = 10;

        endpoint.MaxNumberOfMessages.ShouldBe(10);
    }

    #endregion

    #region the FIFO verdict

    [Fact]
    public void a_fifo_queue_refuses_native_acks_at_bootstrap()
    {
        var endpoint = queue("orders.fifo");
        endpoint.IsListener = true;
        endpoint.Mode = EndpointMode.NativeAck;

        var problem = ListenerConfigurationValidator.Validate(endpoint).ShouldHaveSingleItem();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
        problem.Message.ShouldContain("orders.fifo");
        problem.Message.ShouldContain("ProcessInParallelWithNativeAcks()");
        problem.Message.ShouldContain("ProcessInline()");
    }

    /// <summary>
    ///     Partitioning by group id is the pairing that looks like it ought to rescue FIFO -- SQS MessageGroupId
    ///     is the broker-side analogue of Envelope.GroupId. It does not: SQS blocks a message group behind its own
    ///     in-flight head, and under this mode "in flight" includes unbounded lane queue time behind unrelated
    ///     groups that hashed into the same slot. The refusal must not have a partitioned escape hatch.
    /// </summary>
    [Fact]
    public void partitioning_by_group_id_does_not_rescue_a_fifo_queue()
    {
        var endpoint = queue("orders.fifo");
        endpoint.IsListener = true;
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.GroupShardingSlotNumber = PartitionSlots.Five;

        ListenerConfigurationValidator.Validate(endpoint)
            .ShouldHaveSingleItem()
            .Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
    }

    [Fact]
    public void a_fifo_queue_is_perfectly_happy_in_every_other_mode()
    {
        foreach (var mode in new[] { EndpointMode.BufferedInMemory, EndpointMode.Durable, EndpointMode.Inline })
        {
            var endpoint = queue("orders.fifo");
            endpoint.IsListener = true;
            endpoint.Mode = mode;

            ListenerConfigurationValidator.Validate(endpoint)
                .Where(x => x.Message.Contains("FIFO"))
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public void a_standard_queue_is_accepted()
    {
        var endpoint = queue("orders");
        endpoint.IsListener = true;
        endpoint.Mode = EndpointMode.NativeAck;

        ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();
    }

    #endregion
}
