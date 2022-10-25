using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsSenderProtocol : ISenderProtocol
{
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs;
    private readonly AmazonSqsMapper _mapper;
    private readonly ILogger _logger;

    public SqsSenderProtocol(IWolverineRuntime runtime, AmazonSqsQueue queue, IAmazonSQS sqs)
    {
        _queue = queue;
        _sqs = sqs;
        _mapper = queue.BuildMapper(runtime);
        _logger = runtime.Logger;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _queue.InitializeAsync(_logger);
        
        var entries = batch.Messages.Select(CreateOutgoingEntry).ToList();

        var request = new SendMessageBatchRequest(_queue.QueueUrl, entries);

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
        _mapper.MapEnvelopeToOutgoing(x, entry);

        return entry;
    }
}