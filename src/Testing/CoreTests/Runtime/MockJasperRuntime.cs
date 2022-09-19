using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace CoreTests.Runtime;

public class MockWolverineRuntime : IWolverineRuntime
{
    public MockWolverineRuntime()
    {
        
    }

    public EndpointCollection Endpoints => throw new NotSupportedException();

    public WolverineOptions Options { get; } = new WolverineOptions();

    public AdvancedSettings? Advanced { get; } = new AdvancedSettings(null);

    public HandlerGraph Handlers { get; } = new();

    public IHandlerPipeline Pipeline { get; } = Substitute.For<IHandlerPipeline>();
    public IMessageLogger MessageLogger { get; } = Substitute.For<IMessageLogger>();

    public ListenerTracker ListenerTracker { get; } = new(NullLogger.Instance);

    public ISendingAgent CreateSendingAgent(Uri replyUri, ISender sender, Endpoint endpoint)
    {
        throw new NotImplementedException();
    }

    public ISendingAgent GetOrBuildSendingAgent(Uri address, Action<Endpoint> configureNewEndpoint = null)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IListeningAgent> ActiveListeners()
    {
        throw new NotImplementedException();
    }

    public void AddSendingAgent(ISendingAgent sendingAgent)
    {
        throw new NotImplementedException();
    }

    public Endpoint EndpointFor(Uri uri)
    {
        return Substitute.For<Endpoint>(uri);
    }

    public IMessageRouter RoutingFor(Type messageType)
    {
        return Substitute.For<IMessageRouter>();
    }

    public T TryFindExtension<T>() where T : class
    {
        throw new NotImplementedException();
    }

    public IListeningAgent? FindListeningAgent(Uri uri)
    {
        throw new NotImplementedException();
    }

    public IListeningAgent? FindListeningAgent(string endpointName)
    {
        throw new NotImplementedException();
    }

    public CancellationToken Cancellation { get; } = default;

    public IEnvelopePersistence Persistence { get; } = Substitute.For<IEnvelopePersistence>();
    public ILogger Logger { get; } = Substitute.For<ILogger>();

    public void ScheduleLocalExecutionInMemory(DateTimeOffset executionTime, Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public ValueTask StopListenerAsync(Uri uri, TimeSpan? restart)
    {
        throw new NotImplementedException();
    }

    public ListeningStatus ListeningStatusFor(Uri uri)
    {
        throw new NotImplementedException();
    }

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

    public void RegisterMessageType(Type messageType)
    {
        throw new NotImplementedException();
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


    public void Dispose()
    {
    }
}
