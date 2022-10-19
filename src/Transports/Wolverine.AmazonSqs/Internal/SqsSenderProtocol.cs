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
    private readonly AmazonSqsEndpoint _endpoint;
    private readonly IAmazonSQS _sqs;
    private readonly AmazonSqsMapper _mapper;

    public SqsSenderProtocol(IWolverineRuntime runtime, AmazonSqsEndpoint endpoint, IAmazonSQS sqs)
    {
        _endpoint = endpoint;
        _sqs = sqs;
        _mapper = endpoint.BuildMapper(runtime);
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
        _mapper.MapEnvelopeToOutgoing(x, entry);

        return entry;
    }
}