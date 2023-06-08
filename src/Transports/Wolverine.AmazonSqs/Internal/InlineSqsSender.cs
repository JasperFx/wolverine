using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class InlineSqsSender : ISender
{
    private readonly ILogger _logger;
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs;
    
    public InlineSqsSender(IWolverineRuntime runtime, AmazonSqsQueue queue, IAmazonSQS sqs)
    {
        _queue = queue;
        _sqs = sqs;
        _logger = runtime.LoggerFactory.CreateLogger<InlineSqsSender>();
    }

    public bool SupportsNativeScheduledSend { get; } = false;
    public Uri Destination => _queue.Uri;
    public async Task<bool> PingAsync()
    {
        var envelope = Envelope.ForPing(Destination);
        try
        {
            await SendAsync(envelope);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        await _queue.InitializeAsync(_logger);

        var data = EnvelopeSerializer.Serialize(envelope);
        var request = new SendMessageRequest(_queue.QueueUrl, Convert.ToBase64String(data));

        await _sqs.SendMessageAsync(request);
    }
}