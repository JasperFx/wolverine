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
        await _endpoint.InitializeAsync();
        
        var entries = batch.Messages.Select(CreateOutgoingEntry).ToList();

        var request = new SendMessageBatchRequest(_endpoint.QueueUrl, entries);

        try
        {
            var response = await _sqs.SendMessageBatchAsync(request);
            
            // TODO -- going to have to check the response!!!
            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception e)
        {
            await callback.MarkProcessingFailureAsync(batch, e);
        }
    }

    private SendMessageBatchRequestEntry CreateOutgoingEntry(Envelope x)
    {
        var entry = new SendMessageBatchRequestEntry(x.Id.ToString(), Encoding.Default.GetString(x.Data!));
        _endpoint.MapEnvelopeToOutgoing(x, entry);

        return entry;
    }
}