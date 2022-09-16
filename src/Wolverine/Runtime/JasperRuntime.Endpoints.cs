using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.ImTools;
using Wolverine.Util;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    private readonly object _channelLock = new();

    private ImHashMap<string, ISendingAgent> _localSenders = ImHashMap<string, ISendingAgent>.Empty;

    private ImHashMap<Uri, ISendingAgent> _senders = ImHashMap<Uri, ISendingAgent>.Empty!;

    public ISendingAgent CreateSendingAgent(Uri? replyUri, ISender sender, Endpoint endpoint)
    {
        try
        {
            var agent = buildSendingAgent(sender, endpoint);

            agent.ReplyUri = replyUri;

            endpoint.Agent = agent;

            if (sender is ISenderRequiresCallback senderRequiringCallback && agent is ISenderCallback callbackAgent)
            {
                senderRequiringCallback.RegisterCallback(callbackAgent);
            }

            AddSendingAgent(agent);

            return agent;
        }
        catch (Exception e)
        {
            throw new TransportEndpointException(sender.Destination,
                "Could not build sending sendingAgent. See inner exception.", e);
        }
    }

    public IEnumerable<IListeningAgent> ActiveListeners()
    {
        return _listeners.Values;
    }

    public void AddSendingAgent(ISendingAgent sendingAgent)
    {
        _senders = _senders.AddOrUpdate(sendingAgent.Destination, sendingAgent);
    }

    public ISendingAgent GetOrBuildSendingAgent(Uri address, Action<Endpoint>? configureNewEndpoint = null)
    {
        if (address == null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        if (_senders.TryFind(address, out var agent))
        {
            return agent;
        }

        lock (_channelLock)
        {
            return !_senders.TryFind(address, out agent)
                ? buildSendingAgent(address, configureNewEndpoint)
                : agent;
        }
    }

    private readonly Dictionary<Uri, ListeningAgent> _listeners = new();

    public Endpoint? EndpointFor(Uri uri)
    {
        return Options.endpoints().FirstOrDefault(x => x.Uri == uri);
    }

    private ISendingAgent buildSendingAgent(ISender sender, Endpoint endpoint)
    {
        // This is for the stub transport in the Storyteller specs
        if (sender is ISendingAgent a)
        {
            return a;
        }

        switch (endpoint.Mode)
        {
            case EndpointMode.Durable:
                return new DurableSendingAgent(sender, Advanced, Logger, MessageLogger,
                    Persistence, endpoint);

            case EndpointMode.BufferedInMemory:
                return new BufferedSendingAgent(Logger, MessageLogger, sender, Advanced, endpoint);

            case EndpointMode.Inline:
                return new InlineSendingAgent(sender, endpoint, MessageLogger, Advanced);
        }

        throw new InvalidOperationException();
    }

    public ISendingAgent AgentForLocalQueue(string queueName)
    {
        if (_localSenders.TryFind(queueName, out var agent))
        {
            return agent;
        }

        agent = GetOrBuildSendingAgent($"local://{queueName}".ToUri());
        _localSenders = _localSenders.AddOrUpdate(queueName, agent);

        return agent;
    }

    private ISendingAgent buildSendingAgent(Uri uri, Action<Endpoint>? configureNewEndpoint)
    {
        var transport = Options.TransportForScheme(uri.Scheme);
        if (transport == null)
        {
            throw new InvalidOperationException(
                $"There is no known transport type that can send to the Destination {uri}");
        }

        if (uri.Scheme == TransportConstants.Local)
        {
            var local = (LocalTransport)transport;
            var agent = local.AddSenderForDestination(uri, this);
            agent.Endpoint.Runtime = this; // This is important for serialization

            AddSendingAgent(agent);

            return agent;
        }

        var endpoint = transport.GetOrCreateEndpoint(uri);
        configureNewEndpoint?.Invoke(endpoint);

        endpoint.Runtime ??= this; // This is important for serialization
        return endpoint.StartSending(this, transport.ReplyEndpoint()?.CorrectedUriForReplies());
    }

    public Endpoint? EndpointByName(string endpointName)
    {
        return Options.AllEndpoints().FirstOrDefault(x => x.Name == endpointName);
    }

    public IListeningAgent? FindListeningAgent(Uri uri)
    {
        if (_listeners.TryGetValue(uri, out var agent))
        {
            return agent;
        }

        return null;
    }

    public IListeningAgent? FindListeningAgent(string endpointName)
    {
        return _listeners.Values.FirstOrDefault(x => x.Endpoint.Name.EqualsIgnoreCase(endpointName));
    }
}
