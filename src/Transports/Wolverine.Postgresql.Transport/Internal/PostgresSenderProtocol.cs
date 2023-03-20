using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Postgresql.Internal;

internal class PostgresSenderProtocol : ISenderProtocol
{
    private readonly PostgresEndpoint _endpoint;
    private readonly IOutgoingMapper<PostgresMessage> _mapper;
    private readonly IWolverineRuntime _runtime;
    private readonly PostgresQueueSender _sender;

    public PostgresSenderProtocol(
        IWolverineRuntime runtime,
        PostgresEndpoint endpoint,
        IOutgoingMapper<PostgresMessage> mapper,
        PostgresQueueSender sender)
    {
        _runtime = runtime;
        _endpoint = endpoint;
        _mapper = mapper;
        _sender = sender;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _endpoint.InitializeAsync(_runtime.Logger);

        var messages = new List<PostgresMessage>();

        foreach (var envelope in batch.Messages)
        {
            try
            {
                var message = new PostgresMessage();
                _mapper.MapEnvelopeToOutgoing(envelope, message);

                messages.Add(message);
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e,
                    "Error trying to translate envelope {Envelope} to a PostgresMessage object. Message will be discarded.",
                    envelope);
            }
        }

        try
        {
            await _sender.SendAsync(messages, _runtime.Cancellation);

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception e)
        {
            await callback.MarkProcessingFailureAsync(batch, e);
        }
    }
}
