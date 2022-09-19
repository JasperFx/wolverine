using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.ImTools;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Configuration;

public class EndpointCollection : IAsyncDisposable
{
    private readonly WolverineRuntime _runtime;

    internal EndpointCollection(WolverineRuntime runtime)
    {
        _runtime = runtime;
        _options = runtime.Options;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _senders.Enumerate())
        {
            var sender = kv.Value;
            if (sender is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (sender is IDisposable d)
            {
                d.Dispose();
            }
        }

        foreach (var value in _listeners.Values)
        {
            await value.DisposeAsync();
        }
    }

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
    private readonly WolverineOptions _options;

    public Endpoint? EndpointFor(Uri uri)
    {
        return _options.endpoints().FirstOrDefault(x => x.Uri == uri);
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
                return new DurableSendingAgent(sender, _options.Advanced, _runtime.Logger, _runtime.MessageLogger,
                    _runtime.Persistence, endpoint);

            case EndpointMode.BufferedInMemory:
                return new BufferedSendingAgent(_runtime.Logger, _runtime.MessageLogger, sender, _runtime.Advanced, endpoint);

            case EndpointMode.Inline:
                return new InlineSendingAgent(sender, endpoint, _runtime.MessageLogger, _runtime.Advanced);
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
        var transport = _options.TransportForScheme(uri.Scheme);
        if (transport == null)
        {
            throw new InvalidOperationException(
                $"There is no known transport type that can send to the Destination {uri}");
        }

        if (uri.Scheme == TransportConstants.Local)
        {
            var local = (LocalTransport)transport;
            var agent = local.AddSenderForDestination(uri, _runtime);
            agent.Endpoint.Runtime = _runtime; // This is important for serialization

            AddSendingAgent(agent);

            return agent;
        }

        var endpoint = transport.GetOrCreateEndpoint(uri);
        configureNewEndpoint?.Invoke(endpoint);

        endpoint.Runtime ??= _runtime; // This is important for serialization
        return endpoint.StartSending(_runtime, transport.ReplyEndpoint()?.CorrectedUriForReplies());
    }

    public Endpoint? EndpointByName(string endpointName)
    {
        return _options.AllEndpoints().FirstOrDefault(x => x.Name == endpointName);
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

    internal async Task StartListeners()
    {
        var listeningEndpoints = _options.SelectMany(x => x.Endpoints())
            .Where(x => x.IsListener).Where(x => x is not LocalQueueSettings);

        foreach (var endpoint in listeningEndpoints)
        {
            var agent = new ListeningAgent(endpoint, _runtime);
            await agent.StartAsync().ConfigureAwait(false);
            _listeners[agent.Uri] = agent;
        }
    }
}

public interface IEndpointSource
{
    Uri Uri { get; }
    Endpoint Build(IWolverineRuntime runtime);
}

public class EndpointSource<T> : IEndpointSource where T : Endpoint
{
    private readonly T _endpoint;

    private readonly List<Action<T>> _configurations = new();

    public EndpointSource(T endpoint)
    {
        _endpoint = endpoint;
    }

    public Uri Uri => _endpoint.Uri;

    public void Configure(Action<T> configure)
    {
        _configurations.Add(configure);
    }

    public T Configure(IWolverineRuntime runtime, IEnumerable<IEndpointPolicy> policies)
    {
        foreach (var policy in policies)
        {
            policy.Apply(_endpoint, runtime);
        }

        foreach (var configuration in _configurations)
        {
            configuration(_endpoint);
        }

        return _endpoint;
    }

    public Endpoint Build(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}

