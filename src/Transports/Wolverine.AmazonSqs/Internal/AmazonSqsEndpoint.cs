using Amazon.SQS.Model;
using Baseline;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsEndpoint : Endpoint, IAmazonSqsListeningEndpoint
{
    private readonly AmazonSqsTransport _parent;
    public string QueueName { get; private set; }

    internal AmazonSqsEndpoint(string queueName, AmazonSqsTransport parent) : base( new Uri($"sqs://{queueName}"),EndpointRole.Application)
    {
        _parent = parent;
        QueueName = queueName;
    }
    
    /// <summary>
    /// The duration (in seconds) that the received messages are hidden from subsequent retrieve
    /// requests after being retrieved by a <code>ReceiveMessage</code> request. The default is
    /// 120.
    /// </summary>
    public int VisibilityTimeout { get; set; } = 120;

    /// <summary>
    /// The duration (in seconds) for which the call waits for a message to arrive in the
    /// queue before returning. If a message is available, the call returns sooner than <code>WaitTimeSeconds</code>.
    /// If no messages are available and the wait time expires, the call returns successfully
    /// with an empty list of messages. Default is 5.
    /// </summary>
    public int WaitTimeSeconds { get; set; } = 5;

    /// <summary>
    /// The maximum number of messages to return. Amazon SQS never returns more messages than
    /// this value (however, fewer messages might be returned). Valid values: 1 to 10. Default:
    /// 10.
    /// </summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    // Set by the AmazonSqsTransport parent
    internal string? QueueUrl { get; private set; }

    private bool _initialized;
    
    internal async ValueTask InitializeAsync()
    {
        if (_initialized) return;

        if (_parent.Client == null)
            throw new InvalidOperationException($"Parent {nameof(AmazonSqsTransport)} has not been initialized");
        
        // TODO -- allow for config on endpoint?
        if (_parent.AutoProvision)
        {
            // TODO -- use the configuration here for FIFO or Standard
            var response = await _parent.Client.CreateQueueAsync(QueueName);

            QueueUrl = response.QueueUrl;
        }

        if (QueueUrl.IsEmpty())
        {
            var response = await _parent.Client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        // TODO -- allow for endpoint by endpoint variance
        if (_parent.AutoPurgeOnStartup)
        {
            await _parent.Client.PurgeQueueAsync(QueueUrl);
        }

        _initialized = true;
    }

    public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
    {
        assertReady();

        return new SqsListener(runtime, this, _parent, receiver);
    }

    private void assertReady()
    {
        if (QueueUrl.IsEmpty()) throw new InvalidOperationException("This endpoint has not yet been initialized");

        if (_parent.Client == null)
            throw new InvalidOperationException("The parent transport has not yet been initialized");
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var protocol = new SqsSenderProtocol(runtime, this, _parent.Client ?? throw new InvalidOperationException("Parent transport has not been initialized"));
        return new BatchedSender(Uri, protocol, runtime.Cancellation,
            runtime.Logger);
    }



    internal void ConfigureRequest(ReceiveMessageRequest request)
    {
        request.WaitTimeSeconds = WaitTimeSeconds;
        request.MaxNumberOfMessages = MaxNumberOfMessages;
        request.VisibilityTimeout = VisibilityTimeout;
    }

    internal AmazonSqsMapper BuildMapper(IWolverineRuntime runtime)
    {
        return new AmazonSqsMapper(this, runtime);
    }
}