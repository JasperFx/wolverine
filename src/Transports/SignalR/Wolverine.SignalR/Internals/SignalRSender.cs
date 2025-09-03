using Microsoft.AspNetCore.SignalR;
using Wolverine.Runtime.Interop;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Internals;

internal class SignalRSender<T> : ISender where T : WolverineHub
{
    private readonly SignalREndpoint<T> _endpoint;
    private readonly IHubContext<T> _context;
    private readonly CloudEventsMapper _mapper;

    public SignalRSender(SignalREndpoint<T> endpoint, IHubContext<T> context, CloudEventsMapper mapper)
    {
        _endpoint = endpoint;
        _context = context;
        _mapper = mapper;
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _endpoint.Uri;
    public async Task<bool> PingAsync()
    {
        try
        {
            await _context.Clients.All.SendAsync("ping");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        // This is controlling which subset of active connections
        // should get the message
        var locator = WebSocketRouting.DetermineLocator(envelope);
        
        // DefaultOperation = "ReceiveMessage" in this case
        // Wolverine users will be able to opt into sending messages to different SignalR
        // operations on the client
        var operation = envelope.TopicName ?? SignalRTransport.DefaultOperation;

        var json = _mapper.WriteToString(envelope);

        return new ValueTask(locator.Find(_context).SendAsync(operation, json));
    }
}

