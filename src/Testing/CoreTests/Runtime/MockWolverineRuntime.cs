using System.Diagnostics.Metrics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace CoreTests.Runtime;

public class NullAgentFamily : IAgentFamily
{
    public string Scheme => "null";
    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return new ValueTask<IReadOnlyList<Uri>>(new List<Uri>());
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        throw new NotImplementedException();
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return new ValueTask<IReadOnlyList<Uri>>(new List<Uri>());
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        return ValueTask.CompletedTask;
    }
}

public class MockWolverineRuntime : IWolverineRuntime, IObserver<IWolverineEvent>
{
    public List<IWolverineEvent> ReceivedEvents { get; } = new();

    public MockWolverineRuntime()
    {
        Tracker.Subscribe(this);
    }

    public IMessageTracker MessageTracking { get; } = Substitute.For<IMessageTracker>();

    void IObserver<IWolverineEvent>.OnCompleted()
    {
    }

    void IObserver<IWolverineEvent>.OnError(Exception error)
    {
    }

    void IObserver<IWolverineEvent>.OnNext(IWolverineEvent value)
    {
        ReceivedEvents.Add(value);
    }

    public HandlerGraph Handlers { get; } = new();

    public IEndpointCollection Endpoints { get; } = Substitute.For<IEndpointCollection>();
    public Meter Meter { get; } = new Meter("Wolverine");
    public ILoggerFactory LoggerFactory { get; } = new LoggerFactory();

    public WolverineOptions Options { get; } = new();

    public DurabilitySettings DurabilitySettings { get; } = new();

    public IReplyTracker Replies { get; } = Substitute.For<IReplyTracker>();

    public IHandlerPipeline Pipeline { get; } = Substitute.For<IHandlerPipeline>();
    public WolverineTracker Tracker { get; } = new(NullLogger.Instance);

    public IMessageRouter RoutingFor(Type messageType)
    {
        return Substitute.For<IMessageRouter>();
    }

    public T? TryFindExtension<T>() where T : class
    {
        throw new NotImplementedException();
    }

    public CancellationToken Cancellation => default;

    public IMessageStore Storage { get; } = Substitute.For<IMessageStore>();
    public ILogger Logger { get; } = Substitute.For<ILogger>();

    public IReadOnlyList<IAncillaryMessageStore> AncillaryStores { get;  } = new List<IAncillaryMessageStore>();

    public void ScheduleLocalExecutionInMemory(DateTimeOffset executionTime, Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public void RegisterMessageType(Type messageType)
    {
        throw new NotImplementedException();
    }

    public IMessageInvoker FindInvoker(Type messageType)
    {
        throw new NotImplementedException();
    }

    public void AssertHasStarted()
    {
    }

    public IMessageInvoker FindInvoker(string envelopeMessageType)
    {
        throw new NotImplementedException();
    }

    public IAgentRuntime Agents { get; } = Substitute.For<IAgentRuntime>();

    public bool TryFindMessageType(string? messageTypeName, out Type messageType)
    {
        throw new NotSupportedException();
    }

    public Type DetermineMessageType(Envelope envelope)
    {
        if (envelope.Message == null)
        {
            if (TryFindMessageType(envelope.MessageType, out var messageType))
            {
                return messageType;
            }

            throw new InvalidOperationException(
                $"Unable to determine a message type for `{envelope.MessageType}`, the known types are: {Handlers.Chains.Select(x => x.MessageType.ToMessageTypeName()).Join(", ")}");
        }

        if (envelope.Message == null)
        {
            throw new ArgumentNullException(nameof(Envelope.Message));
        }

        return envelope.Message.GetType();
    }

    public ISendingAgent AddSubscriber(Uri? replyUri, ISender sender, Endpoint endpoint)
    {
        throw new NotImplementedException();
    }

    public ISendingAgent GetOrBuildSendingAgent(Uri address)
    {
        return Substitute.For<ISendingAgent>();
    }

    public void AddListener(IListener listener, Endpoint settings)
    {
        throw new NotImplementedException();
    }

    public IMessageContext NewContext()
    {
        return new MessageContext(this);
    }

    public void AddListener(Endpoint endpoint, IListener agent)
    {
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public IReceiver BuildDurableListener(IListener agent)
    {
        throw new NotImplementedException();
    }

    public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();
    public IWolverineObserver Observer { get; set; } = Substitute.For<IWolverineObserver>();

    public void Dispose()
    {
    }
}