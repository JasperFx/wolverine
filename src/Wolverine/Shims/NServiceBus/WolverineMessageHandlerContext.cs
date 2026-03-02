namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Wolverine-backed implementation of <see cref="IMessageHandlerContext"/>.
/// Delegates all operations to <see cref="IMessageContext"/>.
/// </summary>
public class WolverineMessageHandlerContext : IMessageHandlerContext
{
    private readonly IMessageContext _context;

    public WolverineMessageHandlerContext(IMessageContext context)
    {
        _context = context;
    }

    public string MessageId => _context.Envelope?.Id.ToString() ?? string.Empty;

    public string? ReplyToAddress => _context.Envelope?.ReplyUri?.ToString();

    public IReadOnlyDictionary<string, string?> MessageHeaders =>
        _context.Envelope?.Headers ?? (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>();

    public string? CorrelationId => _context.CorrelationId;

    public async Task Send(object message, SendOptions? options = null)
    {
        var deliveryOptions = options?.ToDeliveryOptions();

        if (options?.Destination != null)
        {
            var endpoint = _context.EndpointFor(options.Destination);
            await endpoint.SendAsync(message, deliveryOptions);
        }
        else
        {
            await _context.SendAsync(message, deliveryOptions);
        }
    }

    public async Task Publish(object message, PublishOptions? options = null)
    {
        await _context.PublishAsync(message, options?.ToDeliveryOptions());
    }

    public async Task Reply(object message, ReplyOptions? options = null)
    {
        await _context.RespondToSenderAsync(message);
    }

    public async Task ForwardCurrentMessageTo(string destination)
    {
        if (_context.Envelope?.Message == null)
        {
            throw new InvalidOperationException("No current message to forward.");
        }

        var endpoint = _context.EndpointFor(destination);
        await endpoint.SendAsync(_context.Envelope.Message);
    }
}
