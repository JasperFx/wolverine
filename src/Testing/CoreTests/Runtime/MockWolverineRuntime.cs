﻿using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace CoreTests.Runtime;

public class MockWolverineRuntime : IWolverineRuntime
{
    public HandlerGraph Handlers { get; } = new();

    public IHostEnvironment Environment { get; } = Substitute.For<IHostEnvironment>();

    public IEndpointCollection Endpoints { get; } = Substitute.For<IEndpointCollection>();

    public WolverineOptions Options { get; } = new();

    public AdvancedSettings Advanced { get; } = new(null);

    public IReplyTracker Replies { get; } = Substitute.For<IReplyTracker>();

    public IHandlerPipeline Pipeline { get; } = Substitute.For<IHandlerPipeline>();
    public IMessageLogger MessageLogger { get; } = Substitute.For<IMessageLogger>();

    public ListenerTracker ListenerTracker { get; } = new(NullLogger.Instance);

    public IMessageRouter RoutingFor(Type messageType)
    {
        return Substitute.For<IMessageRouter>();
    }

    public T? TryFindExtension<T>() where T : class
    {
        throw new NotImplementedException();
    }

    public CancellationToken Cancellation { get; } = default;

    public IMessageStore Storage { get; } = Substitute.For<IMessageStore>();
    public ILogger Logger { get; } = Substitute.For<ILogger>();

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


    public void Dispose()
    {
    }
}