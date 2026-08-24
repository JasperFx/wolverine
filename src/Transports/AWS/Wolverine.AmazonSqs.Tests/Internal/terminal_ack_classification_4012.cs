using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Xunit;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-4012 item 3. Before this, SQS treated every delete failure as transient, so a permanent one burned
/// the whole retry budget before being dropped. SQS classifies better than either sibling transport: it
/// raises distinct typed exceptions, so the terminal set is named rather than inferred from broker text
/// (RabbitMQ) or a boolean (Azure Service Bus).
/// </summary>
public class terminal_ack_classification_4012
{
    private static IAmazonSQS clientThatThrows(Exception exception)
    {
        var client = Substitute.For<IAmazonSQS>();
        client.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeleteMessageResponse>>(_ => throw exception);
        return client;
    }

    private static Task deleteAsync(IAmazonSQS client)
    {
        return SqsSettlement.DeleteAsync(client, "https://sqs.test/q", "receipt-handle",
            NullLogger.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task terminal_failures_are_swallowed_so_the_retry_block_stops()
    {
        Exception[] terminals =
        [
            new ReceiptHandleIsInvalidException("handle is not valid"),
            new MessageNotInflightException("visibility already lapsed"),
            new QueueDoesNotExistException("queue is gone")
        ];

        foreach (var terminal in terminals)
        {
            SqsSettlement.IsTerminal(terminal).ShouldBeTrue($"{terminal.GetType().Name} should be terminal");

            // Swallowing is what stops the RetryBlock. The message is simply not deleted; SQS makes it
            // visible again on its own clock and redelivers it
            await deleteAsync(clientThatThrows(terminal));
        }
    }

    [Fact]
    public async Task transient_failures_still_propagate_so_the_retry_block_retries()
    {
        Exception[] transients =
        [
            new RequestThrottledException("slow down"),
            new OverLimitException("too many inflight"),
            new AmazonSQSException("some other service fault")
        ];

        foreach (var transient in transients)
        {
            SqsSettlement.IsTerminal(transient).ShouldBeFalse($"{transient.GetType().Name} should NOT be terminal");

            // These are exactly the failures the retry budget exists for. Swallowing them would turn a
            // recoverable blip into a message that silently never gets deleted
            await Should.ThrowAsync<Exception>(() => deleteAsync(clientThatThrows(transient)));
        }
    }

    [Fact]
    public async Task a_successful_delete_is_left_alone()
    {
        var client = Substitute.For<IAmazonSQS>();
        client.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse());

        await deleteAsync(client);

        await client.Received(1).DeleteMessageAsync("https://sqs.test/q", "receipt-handle",
            Arg.Any<CancellationToken>());
    }
}
