using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.RemoteInvocation;

namespace Wolverine;

/// <summary>
///     Stand in "spy" for IMessageContext/IMessagePublisher to facilitate unit testing
///     in applications using Wolverine
/// </summary>
public class TestMessageContext : IMessageContext
{
    private readonly List<object> _invoked = new();
    private readonly List<object> _published = new();
    private readonly List<object> _responses = new();
    private readonly List<object> _sent = new();

    public TestMessageContext(object message)
    {
        Envelope = new Envelope(message);
        CorrelationId = Guid.NewGuid().ToString();
    }

    public TestMessageContext() : this(new object())
    {
    }

    /// <summary>
    ///     Messages that were executed inline from this context
    /// </summary>
    public IReadOnlyList<object> Invoked => _invoked;

    /// <summary>
    ///     All messages "published" through this context. If in doubt use AllOutgoing instead.
    /// </summary>
    public IReadOnlyList<object> Published => _published;

    /// <summary>
    ///     All messages "sent" through this context with the SendAsync() semantics that require. If in doubt use AllOutgoing
    ///     instead.
    ///     a subscriber
    /// </summary>
    public IReadOnlyList<object> Sent => _sent;

    /// <summary>
    ///     All outgoing messages sent or published or scheduled through this context
    /// </summary>
    public IReadOnlyList<object> AllOutgoing => _published.Concat(_sent).Concat(_responses).ToArray();

    /// <summary>
    ///     Messages that were specifically sent back to the original sender of the
    ///     current message
    /// </summary>
    public IReadOnlyList<object> ResponsesToSender => _responses;

    /// <summary>
    /// Configure request/reply return values
    /// </summary>
    /// <param name="match">Optional filter to control applicability to the inputs</param>
    /// <param name="destination">Optional match on endpoint Uri for matching on explicit destination</param>
    /// <param name=""></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public ExpectationExpression<T> WhenInvokedMessageOf<T>(Func<T, bool>? match = null, Uri? destination = null, string? endpointName = null)
    {
        match ??= _ => true;
        return new ExpectationExpression<T>(this, match, destination, endpointName);
    }

    public class ExpectationExpression<T>
    {
        private readonly TestMessageContext _parent;
        private readonly Func<T, bool> _match;
        private readonly Uri? _destination;
        private readonly string? _endpointName;

        public ExpectationExpression(TestMessageContext parent, Func<T, bool> match, Uri? destination,
            string? endpointName)
        {
            _parent = parent;
            _match = match;
            _destination = destination;
            _endpointName = endpointName;
        }

        /// <summary>
        /// Response with this object when InvokeAsync<TResponse>() is called
        /// </summary>
        /// <param name="response"></param>
        public void RespondWith<TResponse>(TResponse response)
        {
            var expectation = new ExpectedResponse<T>(_match, response, _destination, _endpointName);
            _parent.Expectations.Add(expectation);
        }
    }

    internal List<IExpectedResponse> Expectations { get; } = new();

    internal interface IExpectedResponse
    {
        bool TryMatch<TResponse>(object message, Uri? destination, string? endpointName, out TResponse response);
    }

    internal class ExpectedResponse<T> : IExpectedResponse
    {
        private readonly Func<T, bool> _match;
        private readonly object _response;
        private readonly Uri? _destination;
        private readonly string? _endpointName;

        public ExpectedResponse(Func<T, bool> match, object response, Uri? destination, string? endpointName)
        {
            _match = match;
            _response = response;
            _destination = destination;
            _endpointName = endpointName;
        }

        public bool TryMatch<TResponse>(object message, Uri? destination, string? endpointName, out TResponse response)
        {
            if (message is T t && _match(t) && _response is TResponse r)
            {
                // Guard on destination match
                if (destination != null && _destination != null && destination != _destination)
                {
                    response = default!;
                    return false;
                }

                // Guard on possible endpoint match
                if (endpointName.IsNotEmpty() && _endpointName.IsNotEmpty() && endpointName != _endpointName)
                {
                    response = default!;
                    return false;
                }

                response = r;
                return true;
            }

            response = default!;
            return false;
        }
    }

    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default,
        TimeSpan? timeout = default)
    {
        var envelope = new Envelope(message)
        {
            TenantId = tenantId
        };

        _invoked.Add(envelope);

        var response = findResponse<T>(message);
        return Task.FromResult(response);
    }

    private TResponse findResponse<TResponse>(object message, Uri? destination = null, string? endpointName = null)
    {
        foreach (var expectation in Expectations)
        {
            if (expectation.TryMatch<TResponse>(message, destination, endpointName, out var response)) return response;
        }

        throw new Exception(
            $"There is no matching expectation for the request message {message} of type {typeof(TResponse).FullNameInCode()}");
    }

    public IDestinationEndpoint EndpointFor(string endpointName)
    {
        return new DestinationEndpoint(this, null, endpointName);
    }

    public IDestinationEndpoint EndpointFor(Uri uri)
    {
        return new DestinationEndpoint(this, uri, uri.ToString());
    }

    public string? CorrelationId { get; set; }
    public Envelope? Envelope { get; }

    Task ICommandBus.InvokeAsync(object message, CancellationToken cancellation, TimeSpan? timeout)
    {
        _invoked.Add(message);
        return Task.CompletedTask;
    }

    Task<T> ICommandBus.InvokeAsync<T>(object message, CancellationToken cancellation, TimeSpan? timeout)
        where T : default
    {
        var envelope = new Envelope(message)
        {

        };

        _invoked.Add(envelope);

        var response = findResponse<T>(message);
        return Task.FromResult(response);
    }

    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default,
        TimeSpan? timeout = default)
    {
        var envelope = new Envelope(message) { TenantId = tenantId };
        _invoked.Add(envelope);
        return Task.CompletedTask;
    }

    IReadOnlyList<Envelope> IMessageBus.PreviewSubscriptions(object message)
    {
        throw new NotSupportedException("This function is not yet supported within the TestMessageContext");
    }

    ValueTask IMessageBus.SendAsync<T>(T message, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message };
        options?.Override(envelope);

        _sent.Add(envelope);

        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBus.PublishAsync<T>(T message, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message };
        options?.Override(envelope);

        _published.Add(envelope);

        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBus.BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message, TopicName = topicName };
        options?.Override(envelope);

        _published.Add(envelope);

        return ValueTask.CompletedTask;
    }

    public string? TenantId { get; set; }

    ValueTask IMessageContext.RespondToSenderAsync(object response)
    {
        _responses.Add(response);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     All scheduled outgoing (to external message transports) messages
    /// </summary>
    /// <returns></returns>
    public IReadOnlyList<Envelope> ScheduledMessages()
    {
        return AllOutgoing
            .OfType<Envelope>()
            .Where(x => x.Status == EnvelopeStatus.Scheduled)
            .ToArray();
    }

    internal class DestinationEndpoint : IDestinationEndpoint
    {
        private readonly Uri? _destination;
        private readonly string? _endpointName;
        private readonly TestMessageContext _parent;

        public DestinationEndpoint(TestMessageContext parent, Uri? destination, string? endpointName)
        {
            _parent = parent;
            _destination = destination;
            _endpointName = endpointName;
        }

        public Uri Uri => _destination!;
        public string EndpointName => _endpointName!;

        public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
        {
            var envelope = new Envelope { Message = message, Destination = _destination, EndpointName = _endpointName };
            options?.Override(envelope);

            _parent._sent.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default,
            TimeSpan? timeout = null)
        {
            var envelope = new Envelope { Message = message, Destination = _destination, EndpointName = _endpointName };
            _parent._sent.Add(envelope);
            return Task.FromResult(new Acknowledgement());
        }

        public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default,
            TimeSpan? timeout = null) where T : class
        {
            var envelope = new Envelope(message)
            {
                EndpointName = _endpointName,
                Destination = _destination
            };

            _parent._invoked.Add(envelope);

            var response = _parent.findResponse<T>(message, _destination, _endpointName);
            return Task.FromResult(response);
        }
    }
}