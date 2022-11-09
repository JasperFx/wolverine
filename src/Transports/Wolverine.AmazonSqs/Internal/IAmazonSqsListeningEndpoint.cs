using Amazon.SQS.Model;

namespace Wolverine.AmazonSqs.Internal;

public interface IAmazonSqsListeningEndpoint
{
    /// <summary>
    /// The duration (in seconds) that the received messages are hidden from subsequent retrieve
    /// requests after being retrieved by a <code>ReceiveMessage</code> request. The default is
    /// 120.
    /// </summary>
    int VisibilityTimeout { get; set; }

    /// <summary>
    /// The duration (in seconds) for which the call waits for a message to arrive in the
    /// queue before returning. If a message is available, the call returns sooner than <code>WaitTimeSeconds</code>.
    /// If no messages are available and the wait time expires, the call returns successfully
    /// with an empty list of messages. Default is 5.
    /// </summary>
    int WaitTimeSeconds { get; set; }

    /// <summary>
    /// The maximum number of messages to return. Amazon SQS never returns more messages than
    /// this value (however, fewer messages might be returned). Valid values: 1 to 10. Default:
    /// 10.
    /// </summary>
    int MaxNumberOfMessages { get; set; }

    /// <summary>
    /// Additional configuration for how an SQS queue should be created
    /// </summary>
    CreateQueueRequest Configuration { get; }
}