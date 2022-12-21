using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsQueue : Endpoint, IAmazonSqsListeningEndpoint, IBrokerQueue
{
    private readonly AmazonSqsTransport _parent;

    private bool _initialized;

    internal AmazonSqsQueue(string queueName, AmazonSqsTransport parent) : base(new Uri($"sqs://{queueName}"),
        EndpointRole.Application)
    {
        _parent = parent;
        QueueName = queueName;
        EndpointName = queueName;

        Configuration = new CreateQueueRequest(QueueName);
    }

    public string QueueName { get; }

    // Set by the AmazonSqsTransport parent
    internal string? QueueUrl { get; private set; }

    /// <summary>
    ///     The duration (in seconds) that the received messages are hidden from subsequent retrieve
    ///     requests after being retrieved by a <code>ReceiveMessage</code> request. The default is
    ///     120.
    /// </summary>
    public int VisibilityTimeout { get; set; } = 120;

    /// <summary>
    ///     The duration (in seconds) for which the call waits for a message to arrive in the
    ///     queue before returning. If a message is available, the call returns sooner than <code>WaitTimeSeconds</code>.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5.
    /// </summary>
    public int WaitTimeSeconds { get; set; } = 5;

    /// <summary>
    ///     The maximum number of messages to return. Amazon SQS never returns more messages than
    ///     this value (however, fewer messages might be returned). Valid values: 1 to 10. Default:
    ///     10.
    /// </summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>
    ///     Additional configuration for how an SQS queue should be created
    /// </summary>
    public CreateQueueRequest Configuration { get; }

    public async ValueTask<bool> CheckAsync()
    {
        var response = await _parent.Client!.GetQueueUrlAsync(QueueName);
        return response.QueueUrl.IsNotEmpty();
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        var client = _parent.Client!;

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        if (QueueUrl.IsEmpty())
        {
            return;
        }

        await client.DeleteQueueAsync(new DeleteQueueRequest(QueueUrl));
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(SetupAsync(_parent.Client!));
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return new ValueTask(PurgeAsync(_parent.Client!));
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = _parent.Client!;

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        var atts = await client.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = QueueUrl
        });

        return new Dictionary<string, string>
        {
            { "name", QueueName },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessages),
                atts.ApproximateNumberOfMessages.ToString()
            },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesDelayed),
                atts.ApproximateNumberOfMessagesDelayed.ToString()
            },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesNotVisible),
                atts.ApproximateNumberOfMessagesNotVisible.ToString()
            }
        };
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        // TODO -- do some logging here?
        if (_initialized)
        {
            return;
        }

        var client = _parent.Client;

        if (client == null)
        {
            throw new InvalidOperationException($"Parent {nameof(AmazonSqsTransport)} has not been initialized");
        }

        // TODO -- allow for config on endpoint?
        if (_parent.AutoProvision)
        {
            await SetupAsync(client);
        }

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        // TODO -- allow for endpoint by endpoint variance
        if (_parent.AutoPurgeAllQueues)
        {
            await client.PurgeQueueAsync(QueueUrl);
        }

        _initialized = true;
    }

    internal async Task SetupAsync(IAmazonSQS client)
    {
        Configuration.QueueName = QueueName;
        var response = await client.CreateQueueAsync(Configuration);

        QueueUrl = response.QueueUrl;
    }

    public async Task PurgeAsync(IAmazonSQS client)
    {
        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        await client.PurgeQueueAsync(QueueUrl);
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (_parent.Client == null)
        {
            throw new InvalidOperationException("The parent transport has not yet been initialized");
        }

        if (QueueUrl.IsEmpty())
        {
            await InitializeAsync(runtime.Logger);
        }

        return new SqsListener(runtime, this, _parent, receiver);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var protocol = new SqsSenderProtocol(runtime, this,
            _parent.Client ?? throw new InvalidOperationException("Parent transport has not been initialized"));
        return new BatchedSender(Uri, protocol, runtime.Cancellation,
            runtime.Logger);
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Inline;
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


    public async Task TeardownAsync(IAmazonSQS client, CancellationToken token)
    {
        if (QueueUrl == null)
        {
            try
            {
                QueueUrl = (await client.GetQueueUrlAsync(QueueName, token)).QueueUrl;
            }
            catch (Exception)
            {
                return;
            }
        }

        await client.DeleteQueueAsync(new DeleteQueueRequest
        {
            QueueUrl = QueueUrl
        }, token);
    }
}