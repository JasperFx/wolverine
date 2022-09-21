using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Logging;
using Wolverine.Util;
using Lamar;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public class CommandBus : ICommandBus
{
    // TODO -- smelly that this is protected, stop that!
    // ReSharper disable once InconsistentNaming
    protected readonly List<Envelope> _outstanding = new();

    [DefaultConstructor]
    public CommandBus(IWolverineRuntime runtime) : this(runtime, Activity.Current?.RootId ?? Guid.NewGuid().ToString())
    {
    }

    internal CommandBus(IWolverineRuntime runtime, string? correlationId)
    {
        Runtime = runtime;
        Persistence = runtime.Persistence;
        CorrelationId = correlationId;
    }

    public string? CorrelationId { get; set; }

    public IWolverineRuntime Runtime { get; }
    public IEnvelopePersistence Persistence { get; }


    public IEnumerable<Envelope> Outstanding => _outstanding;

    public IEnvelopeTransaction? Transaction { get; protected set; }
    public Guid ConversationId { get; protected set; }


    public Task InvokeAsync(object message, CancellationToken cancellation = default)
    {
        return Runtime.Pipeline.InvokeNowAsync(new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            CorrelationId = CorrelationId,
            ConversationId = ConversationId,
            Source = Runtime.Advanced.ServiceName
        }, cancellation);
    }

    public async Task<T?> InvokeAsync<T>(object message, CancellationToken cancellation = default)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelope = new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            ReplyRequested = typeof(T).ToMessageTypeName(),
            ResponseType = typeof(T),
            CorrelationId = CorrelationId,
            ConversationId = ConversationId,
            Source = Runtime.Advanced.ServiceName
        };

        await Runtime.Pipeline.InvokeNowAsync(envelope, cancellation);

        if (envelope.Response == null)
        {
            return default;
        }

        return (T)envelope.Response;
    }

    public ValueTask EnqueueAsync<T>(T message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelope = Runtime.RoutingFor(message.GetType()).RouteLocal(message, null); // TODO -- propagate DeliveryOptions
        envelope.CorrelationId = CorrelationId;
        envelope.ConversationId = ConversationId;
        envelope.Source = Runtime.Advanced.ServiceName;

        return persistOrSendAsync(envelope);
    }

    public ValueTask EnqueueAsync<T>(T message, string workerQueueName)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelope = Runtime.RoutingFor(message.GetType()).RouteLocal(message, workerQueueName, null); // TODO -- propagate DeliveryOptions
        envelope.CorrelationId = CorrelationId;
        envelope.ConversationId = ConversationId;
        envelope.Source = Runtime.Advanced.ServiceName;

        return persistOrSendAsync(envelope);
    }

    public async Task<Guid> ScheduleAsync<T>(T message, DateTimeOffset executionTime)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // TODO -- there's quite a bit of duplication here. Change that!
        var envelope = new Envelope(message)
        {
            ScheduledTime = executionTime,
            Destination = TransportConstants.DurableLocalUri,
            CorrelationId = CorrelationId,
            ConversationId = ConversationId,
            Source = Runtime.Advanced.ServiceName
        };

        // TODO -- memoize this.
        var endpoint = Runtime.Endpoints.EndpointFor(TransportConstants.DurableLocalUri);

        var writer = endpoint!.DefaultSerializer;
        envelope.Data = writer!.Write(envelope);
        envelope.ContentType = writer.ContentType;

        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        await ScheduleEnvelopeAsync(envelope);

        return envelope.Id;
    }

    public Task<Guid> ScheduleAsync<T>(T message, TimeSpan delay)
    {
        return ScheduleAsync(message, DateTimeOffset.UtcNow.Add(delay));
    }

    internal Task ScheduleEnvelopeAsync(Envelope envelope)
    {
        if (envelope.Message == null)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope.Message is required");
        }

        if (!envelope.ScheduledTime.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "No value for ExecutionTime");
        }


        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.Status = EnvelopeStatus.Scheduled;

        if (Transaction != null)
        {
            return Transaction.ScheduleJobAsync(envelope);
        }

        if (Persistence is NullEnvelopePersistence)
        {
            Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime.Value, envelope);
            return Task.CompletedTask;
        }

        return Persistence.ScheduleJobAsync(envelope);
    }

    protected async ValueTask persistOrSendAsync(Envelope envelope)
    {
        if (envelope.Sender is null)
        {
            throw new InvalidOperationException("Envelope has not been routed");
        }

        if (Transaction is not null)
        {
            _outstanding.Fill(envelope);

            if (envelope.Sender.IsDurable)
            {
                await Transaction.PersistAsync(envelope);
            }

            return;
        }

        await envelope.StoreAndForwardAsync();
    }

    public void EnlistInOutbox(IEnvelopeTransaction transaction)
    {
        Transaction = transaction;
    }

    public Task EnlistInOutboxAsync(IEnvelopeTransaction transaction)
    {
        var original = Transaction;
        Transaction = transaction;

        return original == null
            ? Task.CompletedTask
            : original.CopyToAsync(transaction);
    }
}
