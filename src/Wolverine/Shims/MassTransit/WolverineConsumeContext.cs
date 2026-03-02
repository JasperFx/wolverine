namespace Wolverine.Shims.MassTransit;

/// <summary>
/// Wolverine-backed implementation of <see cref="ConsumeContext{T}"/>.
/// Delegates all operations to <see cref="IMessageContext"/>.
/// </summary>
/// <typeparam name="T">The message type</typeparam>
public class WolverineConsumeContext<T> : ConsumeContext<T> where T : class
{
    private readonly IMessageContext _context;

    public WolverineConsumeContext(IMessageContext context, T message)
    {
        _context = context;
        Message = message;
    }

    public T Message { get; }

    public Guid? MessageId => _context.Envelope?.Id;

    public string? CorrelationId => _context.CorrelationId;

    public Guid? ConversationId => _context.Envelope?.ConversationId;

    public IReadOnlyDictionary<string, string?> Headers =>
        _context.Envelope?.Headers ?? (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>();

    public async Task Publish<TMessage>(TMessage message) where TMessage : class
    {
        await _context.PublishAsync(message);
    }

    public async Task Send<TMessage>(TMessage message) where TMessage : class
    {
        await _context.SendAsync(message);
    }

    public async Task Send<TMessage>(TMessage message, Uri destinationAddress) where TMessage : class
    {
        var endpoint = _context.EndpointFor(destinationAddress);
        await endpoint.SendAsync(message);
    }

    public async Task RespondAsync<TMessage>(TMessage message) where TMessage : class
    {
        await _context.RespondToSenderAsync(message);
    }
}
