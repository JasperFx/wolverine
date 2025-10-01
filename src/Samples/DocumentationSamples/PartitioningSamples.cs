using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.RabbitMQ;
using Wolverine.Runtime.Partitioning;

namespace DocumentationSamples;

public class PartitioningSamples
{
    public static async Task listening_with_partitioned_processing()
    {
        #region sample_configuring_partitioned_processing_on_any_listener

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UseRabbitMq();

            // You still need rules for determining the message group id
            // of incoming messages!
            opts.MessagePartitioning
                .ByMessage<IOrderCommand>(x => x.OrderId);
            
            // We're going to listen
            opts.ListenToRabbitQueue("incoming")
                // To really keep our system from processing Order related
                // messages for the same order id concurrently, we'll
                // make it so that only one node actively processes messages
                // from this queue
                .ExclusiveNodeWithParallelism()

                // We're going to partition the message processing internally
                // based on the message group id while allowing up to 7 parallel
                // messages to be executed at once
                .PartitionProcessingByGroupId(PartitionSlots.Seven);
        });

        #endregion
    }
    
    public static async Task configure_local_partitioning()
    {
        #region sample_opting_into_local_partitioned_routing

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.MessagePartitioning
                // First, we're going to tell Wolverine how to determine the 
                // message group id 
                .ByMessage<IOrderCommand>(x => x.OrderId)

                // Next we're setting up a publishing rule to local queues 
                .PublishToPartitionedLocalMessaging("orders", 4, topology =>
                {
                    topology.MessagesImplementing<IOrderCommand>();
                    
                    
                    // this feature exists
                    topology.MaxDegreeOfParallelism = PartitionSlots.Five;
                    
                    // Just showing you how to make additional Wolverine configuration
                    // for all the local queues built from this usage
                    topology.ConfigureQueues(queue =>
                    {
                        queue.TelemetryEnabled(true);
                    });
                });
        });

        #endregion
    }

    public class MySpecialGroupingRule : IGroupingRule
    {
        public bool TryFindIdentity(Envelope envelope, out string groupId)
        {
            throw new NotImplementedException();
        }
    }

    public static async Task configuring_message_grouping_rules()
    {
        #region sample_configuring_message_grouping_rules

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.MessagePartitioning
                // Use saga identity or aggregate handler workflow identity
                // from messages as the group id
                .UseInferredMessageGrouping()

                // First, we're going to tell Wolverine how to determine the 
                // message group id for any message type that can be 
                // cast to this interface. Also works for concrete types too
                .ByMessage<IOrderCommand>(x => x.OrderId)

                // Use the Envelope.TenantId as the message group id
                // this could be valuable to partition work by tenant
                .ByTenantId()

                // Use a custom rule implementing IGroupingRULE with explicit code to determine
                // the group id
                .ByRule(new MySpecialGroupingRule());
        });

        #endregion
    }

    #region sample_send_message_with_group_id

    public static async Task SendMessageToGroup(IMessageBus bus)
    {
        await bus.PublishAsync(
            new ApproveInvoice("AAA"), 
            new() { GroupId = "agroup" });
    }

    #endregion
}

public record PayInvoice(string Id);

[WolverineIgnore]
public static class ApproveInvoiceHandler
{
    #region sample_using_with_group_id_as_cascading_message

    public static IEnumerable<object> Handle(ApproveInvoice command)
    {
        yield return new PayInvoice(command.Id).WithGroupId("aaa");
    }

    #endregion
}

#region sample_order_commands_for_partitioning

public interface IOrderCommand
{
    public string OrderId { get; }
}

public record ApproveOrder(string OrderId) : IOrderCommand;
public record CancelOrder(string OrderId) : IOrderCommand;

#endregion