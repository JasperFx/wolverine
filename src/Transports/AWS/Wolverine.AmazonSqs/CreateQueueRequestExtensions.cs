using Amazon.SQS;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs;

public static class CreateQueueRequestExtensions
{
    /// <summary>
    ///     Override the maximum message size for this SQS queue. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    /// </summary>
    /// <param name="queue"></param>
    /// <param name="maximumSize"></param>
    /// <returns></returns>
    public static AmazonSqsQueue MaximumMessageSize(this AmazonSqsQueue queue, int maximumSize)
    {
        queue.Configuration.Attributes ??= new();
        queue.Configuration.Attributes[QueueAttributeName.MaximumMessageSize] = maximumSize.ToString();

        return queue;
    }

    /// <summary>
    ///     The length of time, in seconds, for which Amazon SQS retains a message. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    /// </summary>
    /// <param name="queue"></param>
    /// <param name="numberOfSeconds"></param>
    /// <returns></returns>
    public static AmazonSqsQueue MessageRetentionPeriod(this AmazonSqsQueue queue, int numberOfSeconds)
    {
        queue.Configuration.Attributes ??= new();
        queue.Configuration.Attributes[QueueAttributeName.MessageRetentionPeriod] = numberOfSeconds.ToString();
        return queue;
    }

    /// <summary>
    ///     The length of time, in seconds, for which a ReceiveMessage action waits for a message to arrive. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    /// </summary>
    /// <param name="queue"></param>
    /// <param name="numberOfSeconds"></param>
    /// <returns></returns>
    public static AmazonSqsQueue ReceiveMessageWaitTimeSeconds(this AmazonSqsQueue queue, int numberOfSeconds)
    {
        queue.Configuration.Attributes ??= new();
        queue.Configuration.Attributes[QueueAttributeName.ReceiveMessageWaitTimeSeconds] = numberOfSeconds.ToString();
        return queue;
    }

    /// <summary>
    ///     The visibility timeout for the queue, in seconds. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    /// </summary>
    /// <param name="queue"></param>
    /// <param name="numberOfSeconds"></param>
    /// <returns></returns>
    public static AmazonSqsQueue VisibilityTimeout(this AmazonSqsQueue queue, int numberOfSeconds)
    {
        queue.Configuration.Attributes ??= new();
        queue.Configuration.Attributes[QueueAttributeName.VisibilityTimeout] = numberOfSeconds.ToString();
        return queue;
    }
}