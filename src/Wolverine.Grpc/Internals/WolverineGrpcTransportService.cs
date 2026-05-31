using Google.Protobuf;
using Grpc.Core;
using JasperFx.Core;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Grpc.Internals;

internal class WolverineGrpcTransportService : WolverineTransport.WolverineTransportBase
{
    private readonly IReceiver _receiver;
    private readonly IListener _listener;
    private readonly WolverineRuntime _runtime;

    public WolverineGrpcTransportService(IReceiver receiver, IListener listener, IWolverineRuntime runtime)
    {
        _receiver = receiver;
        _listener = listener;
        _runtime = (WolverineRuntime)runtime;
    }

    public override async Task<Ack> Send(WolverineMessage request, ServerCallContext context)
    {
        var envelope = EnvelopeSerializer.Deserialize(request.Data.ToByteArray());
        await _receiver.ReceivedAsync(_listener, envelope);
        return new Ack { Success = true };
    }

    public override async Task<Ack> SendBatch(WolverineMessageBatch request, ServerCallContext context)
    {
        var envelopes = EnvelopeSerializer.ReadMany(request.Data.ToByteArray());
        await _receiver.ReceivedAsync(_listener, envelopes);
        return new Ack { Success = true };
    }

    public override Task<Ack> Ping(PingRequest request, ServerCallContext context)
    {
        return Task.FromResult(new Ack { Success = true });
    }

    // GH-2967 inline request/reply: run the handler chain for the inbound envelope and return the reply
    // envelope in the unary response. Mirrors the HTTP transport's /_wolverine/invoke executor. A
    // handler failure comes back as a FailureAcknowledgement reply (the sender rethrows
    // WolverineRequestReplyException), and the outbox is flushed (InvokeInlineAsync) before the response.
    public override async Task<WolverineMessage> Call(WolverineMessage request, ServerCallContext context)
    {
        Envelope envelope;
        try
        {
            envelope = EnvelopeSerializer.Deserialize(request.Data.ToByteArray());
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid envelope: " + e.Message));
        }

        envelope.DoNotCascadeResponse = true;
        if (envelope.ContentType.IsNotEmpty())
        {
            envelope.Serializer = _runtime.Options.FindSerializer(envelope.ContentType!);
        }

        var deserializeResult = await _runtime.Pipeline.TryDeserializeEnvelope(envelope);
        if (deserializeResult != NullContinuation.Instance)
        {
            return failureReply(envelope, deserializeResult is NoHandlerContinuation
                ? $"No handler for the requested message type {envelope.MessageType}"
                : $"Execution error for requested message type {envelope.MessageType}");
        }

        if (envelope.ReplyRequested.IsNotEmpty())
        {
            if (_runtime.Handlers.TryFindMessageType(envelope.ReplyRequested, out var responseType))
            {
                envelope.ResponseType = responseType;
            }
            else
            {
                return failureReply(envelope, $"Unknown reply requested message type of {envelope.ReplyRequested}");
            }
        }

        if (_runtime.FindInvoker(envelope.MessageType!) is not Executor executor)
        {
            return failureReply(envelope, $"Unable to find a message executor for {envelope.MessageType}");
        }

        try
        {
            await executor.InvokeInlineAsync(envelope, context.CancellationToken);
        }
        catch (Exception e)
        {
            return failureReply(envelope, e.Message);
        }

        // Mark the request complete on every success path (mirrors the HTTP executor) so tracked
        // sessions quiesce — the request's record needs this terminal event.
        _runtime.MessageTracking.MessageSucceeded(envelope);

        if (envelope.Response != null)
        {
            var response = envelope.CreateForResponse(envelope.Response);
            response.Serializer ??= _runtime.Options.DefaultSerializer;
            response.ContentType = response.Serializer.ContentType;
            response.Data = response.Serializer.WriteMessage(response.Message!);
            return new WolverineMessage { Data = ByteString.CopyFrom(EnvelopeSerializer.Serialize(response)) };
        }

        // No reply message (Ack-style InvokeAsync): return a minimal reply envelope correlated to the
        // request so the sender can complete an Acknowledgement.
        var ack = new Envelope { ConversationId = envelope.Id, Data = Array.Empty<byte>() };
        return new WolverineMessage { Data = ByteString.CopyFrom(EnvelopeSerializer.Serialize(ack)) };
    }

    private static WolverineMessage failureReply(Envelope request, string message)
    {
        var failure = new FailureAcknowledgement { RequestId = request.Id, Message = message };
        var reply = request.CreateForResponse(failure);
        reply.Serializer ??= IntrinsicSerializer.Instance;
        // IntrinsicSerializer only implements Write(Envelope); WriteMessage(object) intentionally throws.
        reply.Data = reply.Serializer.Write(reply);
        reply.ContentType = reply.Serializer.ContentType;
        return new WolverineMessage { Data = ByteString.CopyFrom(EnvelopeSerializer.Serialize(reply)) };
    }
}
