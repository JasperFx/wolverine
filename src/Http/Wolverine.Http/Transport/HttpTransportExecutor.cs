using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

internal class HttpTransportExecutor
{
    public static readonly string EnvelopeContentType = "binary/wolverine-envelope";
    public static readonly string EnvelopeBatchContentType = "binary/wolverine-envelopes";
    
    private readonly WolverineRuntime _runtime;
    private readonly ILogger<HttpTransportExecutor> _logger;

    public HttpTransportExecutor(IWolverineRuntime runtime)
    {
        _runtime = (WolverineRuntime)runtime;
        _logger = runtime.LoggerFactory.CreateLogger<HttpTransportExecutor>();
    }

    public async Task<IResult> ExecuteBatchAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("content-type", out var values))
        {
            if (values[0] != EnvelopeBatchContentType)
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
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to read Envelope[] in the Http Transport ExecuteBatchAsync()");
            return Results.Problem("Error trying to deserialize envelope batch", statusCode: 500);
        }

        try
        {
            var queueName = raw as string ?? TransportConstants.Default;
            var queue = (ILocalQueue)_runtime.Endpoints.AgentForLocalQueue(queueName);

            var nulloListener = new NulloListener($"http://localhost{httpContext.Request.Path}".ToUri());
            await queue.ReceivedAsync(nulloListener, envelopes.ToArray());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when receiving a batch of envelopes inside the Http transport");
            return Results.Problem("Error receiving the messages", statusCode: 500);
        }

        return Results.Ok();
    }

    public async Task<IResult> InvokeAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("content-type", out var values))
        {
            if (values[0] != EnvelopeContentType)
            {
                return Results.StatusCode(415);
            }
        }
        else
        {
            return Results.StatusCode(415);
        }
        
        var data = await httpContext.Request.Body.ReadAllBytesAsync();
        var envelope = EnvelopeSerializer.Deserialize(data);
        envelope.Destination = $"http://localhost{httpContext.Request.Path}".ToUri();
        envelope.DoNotCascadeResponse = true;
        envelope.Serializer = _runtime.Options.FindSerializer(envelope.ContentType);

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


        IExecutor executor = default;

        try
        {
            executor = _runtime.FindInvoker(envelope.MessageType) as Executor;
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
        catch (Exception)
        {
            return Results.Problem("Execution failed", statusCode: 500);
        }

        if (envelope.Response != null)
        {
            var response = envelope.CreateForResponse(envelope.Response);
            if (response.Serializer == null)
            {
                response.Serializer = _runtime.Options.DefaultSerializer;
                response.ContentType = response.Serializer.ContentType;
            }

            response.Data = response.Serializer.WriteMessage(response.Message);

            httpContext.Response.ContentType = "binary/wolverine-envelope";
            var responseData = EnvelopeSerializer.Serialize(response);
            httpContext.Response.ContentLength = responseData.Length;
            await httpContext.Response.Body.WriteAsync(responseData);

            
        }

        _runtime.MessageTracking.MessageSucceeded(envelope);
        return Results.Empty;
    }


}