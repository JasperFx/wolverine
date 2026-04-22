using Google.Protobuf;
using Grpc.Core;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Grpc.Internals;

internal class WolverineGrpcTransportService : WolverineTransport.WolverineTransportBase
{
    private readonly IReceiver _receiver;
    private readonly IListener _listener;

    public WolverineGrpcTransportService(IReceiver receiver, IListener listener)
    {
        _receiver = receiver;
        _listener = listener;
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
}
