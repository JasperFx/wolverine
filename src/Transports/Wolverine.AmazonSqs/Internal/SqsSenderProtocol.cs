using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsSenderProtocol : ISenderProtocol
{
    private readonly AmazonSqsEndpoint _endpoint;
    private readonly IAmazonSQS _sqs;
    private readonly ILogger _logger;

    public SqsSenderProtocol(AmazonSqsEndpoint endpoint, IAmazonSQS sqs, ILogger logger)
    {
        _endpoint = endpoint;
        _sqs = sqs;
        _logger = logger;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        var entries = batch.Messages.Select(CreateOutgoingEntry).ToList();

        var request = new SendMessageBatchRequest(_endpoint.QueueName, entries);

        try
        {
            await _sqs.SendMessageBatchAsync(request);
            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception e)
        {
            await callback.MarkProcessingFailureAsync(batch, e);
        }
    }

    private SendMessageBatchRequestEntry CreateOutgoingEntry(Envelope x)
    {
        var entry = new SendMessageBatchRequestEntry
        {
            MessageBody = Encoding.Default.GetString(x.Data!)
        };

        _endpoint.MapEnvelopeToOutgoing(x, entry);

        return entry;
    }
}