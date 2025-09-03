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
        // TODO -- look at the envelope and decide how to send things out
        throw new NotImplementedException();
    }
}