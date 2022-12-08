using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Transports.Local;

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

    public IDestinationEndpoint EndpointFor(string endpointName)
    {
        return new DestinationEndpoint(this, null, endpointName);
    }

    public IDestinationEndpoint EndpointFor(Uri uri)
    {
        return new DestinationEndpoint(this, uri, uri.ToString());
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

    public string? CorrelationId { get; set; }
    public Envelope? Envelope { get; }

    Task ICommandBus.InvokeAsync(object message, CancellationToken cancellation)
    {
        _invoked.Add(message);
        return Task.CompletedTask;
    }

    Task<T?> ICommandBus.InvokeAsync<T>(object message, CancellationToken cancellation) where T : default
    {
        throw new NotSupportedException("This function is not yet supported within the TestMessageContext");
    }

    Task<Guid> ICommandBus.ScheduleAsync<T>(T message, DateTimeOffset executionTime)
    {
        var envelope = new Envelope
        {
            Message = message, ScheduledTime = executionTime,
            Status = EnvelopeStatus.Scheduled
        };

        _published.Add(envelope);

        return Task.FromResult(envelope.Id);
    }

    Task<Guid> ICommandBus.ScheduleAsync<T>(T message, TimeSpan delay)
    {
        var envelope = new Envelope
        {
            Message = message,
            ScheduleDelay = delay,
            Status = EnvelopeStatus.Scheduled
        };

        _published.Add(envelope);

        return Task.FromResult(envelope.Id);
    }

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message)
    {
        throw new NotSupportedException();
    }

    ValueTask IMessageBus.SendAsync<T>(T message, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message };
        options?.Override(envelope);

        _sent.Add(envelope);

        return new ValueTask();
    }

    ValueTask IMessageBus.PublishAsync<T>(T message, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message };
        options?.Override(envelope);

        _published.Add(envelope);

        return new ValueTask();
    }

    ValueTask IMessageBus.SendToTopicAsync(string topicName, object message, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message, TopicName = topicName };
        options?.Override(envelope);

        _published.Add(envelope);

        return new ValueTask();
    }

    ValueTask IMessageBus.SchedulePublishAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options)
    {
        var envelope = new Envelope
        {
            Message = message, ScheduledTime = time,
            Status = EnvelopeStatus.Scheduled
        };

        options?.Override(envelope);
        _published.Add(envelope);

        return new ValueTask();
    }

    ValueTask IMessageBus.SchedulePublishAsync<T>(T message, TimeSpan delay, DeliveryOptions? options)
    {
        var envelope = new Envelope { Message = message, ScheduleDelay = delay, Status = EnvelopeStatus.Scheduled };
        options?.Override(envelope);
        _published.Add(envelope);

        return new ValueTask();
    }

    Task<Acknowledgement> IMessageBus.SendAndWaitAsync(object message, CancellationToken cancellation,
        TimeSpan? timeout)
    {
        _sent.Add(message);
        return Task.FromResult(new Acknowledgement());
    }

    internal class DestinationEndpoint : IDestinationEndpoint
    {
        private readonly TestMessageContext _parent;
        private readonly Uri? _destination;
        private readonly string? _endpointName;

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
            var envelope = new Envelope { Message = message, Destination = _destination, EndpointName = _endpointName};
            options?.Override(envelope);
            
            _parent._sent.Add(envelope);
            return new ValueTask();
        }

        public Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        {
            var envelope = new Envelope { Message = message, Destination = _destination, EndpointName = _endpointName};
            _parent._sent.Add(envelope);
            return Task.FromResult(new Acknowledgement());
        }

        public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) where T : class
        {
            throw new NotSupportedException();
        }
    }

    Task<T> IMessageBus.RequestAsync<T>(object message, CancellationToken cancellation, TimeSpan? timeout)
    {
        throw new NotSupportedException();
    }

    ValueTask IMessageContext.RespondToSenderAsync(object response)
    {
        _responses.Add(response);

        return new ValueTask();
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
}