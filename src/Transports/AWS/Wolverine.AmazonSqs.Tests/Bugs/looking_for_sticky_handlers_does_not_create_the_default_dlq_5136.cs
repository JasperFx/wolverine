using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;

namespace Wolverine.AmazonSqs.Tests.Bugs;

/// <summary>
/// GH-5136. Sticky handlers always apply now, so building the handler graph asks every handler type which
/// endpoints it is sticky to -- before any listener's delayed configuration has run. Reading the SQS
/// transport's endpoints for that infers the default dead letter queue for a listener whose own dead letter
/// queue has not been configured yet, and the default queue then stays even though no listener uses it.
/// No broker is needed: the handler graph is grouped without starting a host.
/// </summary>
public class looking_for_sticky_handlers_does_not_create_the_default_dlq_5136
{
    [Fact]
    public void grouping_the_handler_graph_does_not_infer_the_default_dead_letter_queue()
    {
        var options = new WolverineOptions();
        options.UseAmazonSqsTransportLocally();

        options.ListenToSqsQueue("sticky-lookup-5136")
            .ConfigureDeadLetterQueue("sticky-lookup-5136-error");

        options.HandlerGraph.AddRange([
            new HandlerCall(typeof(StickyLookupHandler5136), nameof(StickyLookupHandler5136.Handle))
        ]);
        options.HandlerGraph.Group(options);

        options.Transports.GetOrCreate<AmazonSqsTransport>().Queues
            .Contains(AmazonSqsTransport.DeadLetterQueueName)
            .ShouldBeFalse();
    }
}

public record StickyLookupMessage5136;

[WolverineIgnore]
public class StickyLookupHandler5136
{
    public void Handle(StickyLookupMessage5136 message)
    {
    }
}
