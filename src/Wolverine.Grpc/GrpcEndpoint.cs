using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Grpc.Internals;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Grpc;

public class GrpcEndpoint : Endpoint, IInlineRequestReplyEndpoint
{
    private readonly object _clientLock = new();
    private GrpcChannel? _callChannel;
    private WolverineTransport.WolverineTransportClient? _callClient;

    public GrpcEndpoint(Uri uri) : base(uri, EndpointRole.Application)
    {
        Host = uri.Host;
        Port = uri.IsDefaultPort ? 5000 : uri.Port;
        BrokerRole = "grpc";
    }

    public string Host { get; }
    public int Port { get; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var listener = new GrpcListener(Uri, Port, receiver, runtime, runtime.LoggerFactory.CreateLogger<GrpcListener>());
        await listener.StartAsync();
        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new GrpcSender(Uri, Host, Port, runtime.LoggerFactory.CreateLogger<GrpcSender>());
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode is EndpointMode.Inline or EndpointMode.BufferedInMemory;
    }

    // GH-2967: gRPC's unary RPC carries the reply in the same exchange, so InvokeAsync<T> reads the
    // reply envelope straight off the Call(WolverineMessage) response — no ReplyListener, no listening
    // endpoint on the sender. Selected by MessageRoute.For via IInlineRequestReplyEndpoint.
    public async Task<Envelope> InvokeRemoteAsync(Envelope request, IWolverineRuntime runtime, CancellationToken cancellation)
    {
        if (request.Data == null && request.Message != null)
        {
            request.Serializer ??= DefaultSerializer;
            request.Data = request.Serializer!.Write(request);
            request.ContentType ??= request.Serializer!.ContentType;
        }

        var client = resolveCallClient();
        var message = new WolverineMessage { Data = ByteString.CopyFrom(EnvelopeSerializer.Serialize(request)) };

        try
        {
            var response = await client.CallAsync(message, cancellationToken: cancellation);
            return EnvelopeSerializer.Deserialize(response.Data.ToByteArray());
        }
        catch (global::Grpc.Core.RpcException e)
        {
            // Transport-level failure (the receiver couldn't even produce a reply envelope): surface as
            // a failure reply so the caller gets the usual WolverineRequestReplyException.
            var ack = new FailureAcknowledgement
            {
                RequestId = request.Id,
                Message = $"Inline gRPC request/reply to {Uri} failed: {e.Status.StatusCode} {e.Status.Detail}"
            };
            return new Envelope { Message = ack };
        }
    }

    private WolverineTransport.WolverineTransportClient resolveCallClient()
    {
        if (_callClient != null) return _callClient;

        lock (_clientLock)
        {
            _callChannel ??= GrpcChannel.ForAddress($"http://{Host}:{Port}");
            _callClient ??= new WolverineTransport.WolverineTransportClient(_callChannel);
        }

        return _callClient;
    }
}
