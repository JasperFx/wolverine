using System.Text.Json;
using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Interop;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Http.Transport;

internal class HttpTransportExecutor
{
    private readonly WolverineRuntime _runtime;
    private readonly ILogger<HttpTransportExecutor> _logger;

    // How long a batch post to a durable local queue waits for the inbox to store it before asking the sender
    // to retry. A batch stored after the answer is retried anyway, and the inbox deduplicates it by id
    internal static TimeSpan BatchReceiptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public HttpTransportExecutor(IWolverineRuntime runtime)
    {
        _runtime = (WolverineRuntime)runtime;
        _logger = runtime.LoggerFactory.CreateLogger<HttpTransportExecutor>();
    }

    public async Task<IResult> ExecuteBatchAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("content-type", out var values))
        {
            if (values[0] != HttpTransport.EnvelopeBatchContentType)
            {
                return Results.StatusCode(415);
            }
        }
        else
        {
            return Results.StatusCode(415);
        }
        
        var data = await httpContext.Request.Body.ReadAllBytesAsync();
        httpContext.Request.RouteValues.TryGetValue("queue", out var raw);

        Envelope[] envelopes = Array.Empty<Envelope>();
        try
        {
            envelopes = EnvelopeSerializer.ReadMany(data);
        }
        catch (InvalidEnvelopeException e)
        {
            _logger.LogWarning(e, "Rejected malformed envelope batch in Http Transport ExecuteBatchAsync()");
            return Results.Problem($"Invalid envelope: {e.Message}", statusCode: 400);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to read Envelope[] in the Http Transport ExecuteBatchAsync()");
            return Results.Problem("Error trying to deserialize envelope batch", statusCode: 500);
        }

        try
        {
            var queueName = raw as string ?? TransportConstants.Default;
            var queue = (ILocalQueue)_runtime.Endpoints.AgentForLocalQueue(queueName);

            // GH-4807: stamp the local queue that is actually receiving this batch, not the synthesized
            // "http://localhost{request path}" address this used to invent. MarkReceived() copies the
            // listener's address onto Envelope.Destination, which is what the durable inbox persists as
            // received_at -- and inbox recovery resolves an ownerless row by looking up a listener circuit
            // by that address. Nothing is ever registered under the request path (the HTTP transport
            // receives by push and registers no listener at all), so a replayed dead letter or the rows of
            // a dead node were skipped on every durability pass, silently, forever. The local queue's own
            // address is both resolvable and the truth about where the envelope is sitting.
            if (queue is DurableLocalQueue)
            {
                // GH-5276. The durable receiver returns before it has stored the batch, and when the inbox write
                // fails it defers each envelope back to the listener -- which over HTTP used to be a NulloListener
                // that did nothing, after this had already answered 200 and the sender had deleted the envelopes
                // from its outbox. Answer only once the receiver says where every envelope ended up.
                var listener = new BatchReceiptListener(queue.Uri, envelopes);
                await queue.ReceivedAsync(listener, envelopes.ToArray());

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
                timeout.CancelAfter(BatchReceiptTimeout);

                bool stored;
                try
                {
                    stored = await listener.AllStoredAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    stored = false;
                }

                if (!stored)
                {
                    _logger.LogWarning(
                        "The durable inbox at {Uri} could not store a batch of {Count} envelope(s) received over the Http transport. Asking the sender to retry it",
                        queue.Uri, envelopes.Length);
                    return Results.Problem("The messages could not be stored in the durable inbox", statusCode: 503);
                }
            }
            else
            {
                var nulloListener = new NulloListener(queue.Uri);
                await queue.ReceivedAsync(nulloListener, envelopes.ToArray());
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when receiving a batch of envelopes inside the Http transport");
            return Results.Problem("Error receiving the messages", statusCode: 500);
        }

        return Results.Ok();
    }

    public async Task<IResult> InvokeAsync(HttpContext httpContext, JsonSerializerOptions options)
    {
        if (httpContext.Request.Headers.TryGetValue("content-type", out var values))
        {
            if (values[0] != HttpTransport.EnvelopeContentType &&
                values[0] != "application/cloudevents+json")
            {
                return Results.StatusCode(415);
            }
        }
        else
        {
            return Results.StatusCode(415);
        }

        Envelope? envelope = null;
        if (values[0] == HttpTransport.EnvelopeContentType)
        {
            var data = await httpContext.Request.Body.ReadAllBytesAsync();
            try
            {
                envelope = EnvelopeSerializer.Deserialize(data);
            }
            catch (InvalidEnvelopeException e)
            {
                _logger.LogWarning(e, "Rejected malformed envelope in Http Transport InvokeAsync()");
                return Results.Problem($"Invalid envelope: {e.Message}", statusCode: 400);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to deserialize envelope in Http Transport InvokeAsync()");
                return Results.Problem("Error trying to deserialize envelope", statusCode: 500);
            }
        }
        else
        {
            var cloudEventMapper = new CloudEventsMapper(_runtime.Handlers, options);
            var data = await httpContext.Request.Body.ReadAllTextAsync();
            envelope = new Envelope();
            cloudEventMapper.MapIncoming(envelope, data);
        }

        envelope.Destination = $"http://localhost{httpContext.Request.Path}".ToUri();
        envelope.DoNotCascadeResponse = true;

        // GH-5238. A content type with no registered serializer used to throw out of FindSerializer() and
        // answer 500. It is a request this node can't decode, so say so.
        var serializer = envelope.ContentType.IsEmpty()
            ? _runtime.Options.DefaultSerializer
            : _runtime.Options.TryFindSerializer(envelope.ContentType);

        if (serializer == null)
        {
            _logger.LogWarning("Rejected an envelope with unknown content type {ContentType} in Http Transport InvokeAsync()",
                envelope.ContentType);
            return Results.Problem($"No serializer is registered for content type '{envelope.ContentType}'",
                statusCode: 415);
        }

        envelope.Serializer = serializer;

        var deserializeResult = await _runtime.Pipeline.TryDeserializeEnvelope(envelope);

        if (deserializeResult != NullContinuation.Instance)
        {
            if (deserializeResult is NoHandlerContinuation)
            {
                return Results.Problem($"No handler for the requested message type {envelope.MessageType}", statusCode:400);
            }

            if (deserializeResult is MoveToErrorQueue move)
            {
                _logger.LogError(move.Exception, "Error executing message of type {MessageType}", envelope.MessageType);
                return Results.Problem($"Execution error for requested message type {envelope.MessageType}",
                    statusCode: 500);
            }
        }

        if (envelope.ReplyRequested.IsNotEmpty())
        {
            if (_runtime.Handlers.TryFindMessageType(envelope.ReplyRequested, out var responseType))
            {
                envelope.ResponseType = responseType;
            }
            else
            {
                return Results.Problem($"Unknown reply requested message type of {envelope.MessageType}",
                    statusCode: 400);
            }
        }


        IExecutor? executor = default;

        try
        {
            executor = _runtime.FindInvoker(envelope.MessageType!) as Executor;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to find a message executor for {MessageType}", envelope.MessageType);
            return Results.Problem($"Error trying to find a message executor for {envelope.MessageType}",
                statusCode: 500);
        }

        if (executor == null)
        {
            _logger.LogInformation("Unable to find message executor for {MessageType}", envelope.MessageType);
            return Results.Problem($"Unable to find a message executor for {envelope.MessageType}",
                statusCode: 400);
        }

        try
        {
            await executor.InvokeInlineAsync(envelope, httpContext.RequestAborted);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Inline HTTP invoke failed for message type {MessageType}", envelope.MessageType);

            // GH-2966: surface the handler failure as an envelope-shaped FailureAcknowledgement on a
            // 500 so the inline-request/reply sender re-throws WolverineRequestReplyException — the
            // same caller-visible semantics as the brokered request/reply path.
            var failure = new FailureAcknowledgement { RequestId = envelope.Id, Message = e.Message };
            var failureEnvelope = envelope.CreateForResponse(failure);
            failureEnvelope.Serializer ??= IntrinsicSerializer.Instance;
            // IntrinsicSerializer only implements Write(Envelope) (it reads envelope.Message);
            // WriteMessage(object) intentionally throws. The Message was set by CreateForResponse.
            failureEnvelope.Data = failureEnvelope.Serializer.Write(failureEnvelope);
            failureEnvelope.ContentType = failureEnvelope.Serializer.ContentType;

            httpContext.Response.StatusCode = 500;
            httpContext.Response.ContentType = "binary/wolverine-envelope";
            var failureData = EnvelopeSerializer.Serialize(failureEnvelope);
            httpContext.Response.ContentLength = failureData.Length;
            await httpContext.Response.Body.WriteAsync(failureData);
            return Results.Empty;
        }

        if (envelope.Response != null)
        {
            var response = envelope.CreateForResponse(envelope.Response);
            if (response.Serializer == null)
            {
                response.Serializer = _runtime.Options.DefaultSerializer;
                response.ContentType = response.Serializer.ContentType;
            }

            response.Data = response.Serializer.WriteMessage(response.Message!);

            httpContext.Response.ContentType = "binary/wolverine-envelope";
            var responseData = EnvelopeSerializer.Serialize(response);
            httpContext.Response.ContentLength = responseData.Length;
            await httpContext.Response.Body.WriteAsync(responseData);

            
        }

        _runtime.MessageTracking.MessageSucceeded(envelope);
        return Results.Empty;
    }


}