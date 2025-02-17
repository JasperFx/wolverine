using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Configuration;

public interface IEndpointCollection : IAsyncDisposable
{
    ISendingAgent CreateSendingAgent(Uri? replyUri, ISender sender, Endpoint endpoint);
    IEnumerable<IListeningAgent> ActiveListeners();
    ISendingAgent GetOrBuildSendingAgent(Uri address, Action<Endpoint>? configureNewEndpoint = null);
    Endpoint? EndpointFor(Uri uri);
    ISendingAgent AgentForLocalQueue(string queueName);
    Endpoint? EndpointByName(string endpointName);
    IListeningAgent? FindListeningAgent(Uri uri);
    IListeningAgent? FindListeningAgent(string endpointName);
    Task StartListenersAsync();
    LocalQueue? LocalQueueForMessageType(Type messageType);
    IEnumerable<ISendingAgent> ActiveSendingAgents();
    ISendingAgent? AgentForLocalQueue(Uri uri);

    /// <summary>
    /// Endpoints where the message listener should only be active on a single endpoint
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<Endpoint> ExclusiveListeners();

    Task StartListenerAsync(Endpoint endpoint, CancellationToken cancellationToken);
    Task StopListenerAsync(Endpoint endpoint, CancellationToken cancellationToken);

    IListenerCircuit FindListenerCircuit(Uri address);
}

public class EndpointCollection : IEndpointCollection
{
    private readonly object _channelLock = new();

    private readonly Dictionary<Uri, ListeningAgent> _listeners = new();
    private readonly WolverineOptions _options;
    private readonly WolverineRuntime _runtime;

    private ImHashMap<string, ISendingAgent> _localSenders = ImHashMap<string, ISendingAgent>.Empty;

    private ImHashMap<Uri, ISendingAgent> _senders = ImHashMap<Uri, ISendingAgent>.Empty!;

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
                try
                {
                    await ad.DisposeAsync();
                }
                catch (Exception)
                {
                    // Don't want this being thrown
                }
            }
            else if (sender is IDisposable d)
            {
                d.Dispose();
            }
        }

        foreach (var value in _listeners.Values) await value.DisposeAsync();
    }

    public IEnumerable<ISendingAgent> ActiveSendingAgents()
    {
        return _senders.Enumerate().Select(x => x.Value);
    }

    public ISendingAgent CreateSendingAgent(Uri? replyUri, ISender sender, Endpoint endpoint)
    {
        try
        {
            endpoint.Compile(_runtime);
            var agent = buildSendingAgent(sender, endpoint);
            endpoint.Agent = agent;

            agent.ReplyUri = replyUri;

            endpoint.Agent = agent;

            if (sender is ISenderRequiresCallback senderRequiringCallback && agent is ISenderCallback callbackAgent)
            {
                senderRequiringCallback.RegisterCallback(callbackAgent);
            }

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
            if (_senders.TryFind(address, out agent))
            {
                return agent;
            }

            agent = buildSendingAgent(address, configureNewEndpoint);
            _senders = _senders.AddOrUpdate(address, agent);

            if (agent is DurableLocalQueue || agent is BufferedLocalQueue)
            {
                _localSenders = _localSenders.AddOrUpdate(LocalTransport.QueueName(address), agent);
            }

            _runtime.Observer.EndpointAdded(agent.Endpoint);

            return agent;
        }
    }

    public Endpoint? EndpointFor(Uri uri)
    {
        var endpoint = _options.Transports.SelectMany(x => x.Endpoints()).FirstOrDefault(x => x.Uri == uri);
        endpoint?.Compile(_runtime);

        return endpoint;
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

    public ISendingAgent? AgentForLocalQueue(Uri uri)
    {
        if (_senders.TryFind(uri, out var agent))
        {
            return agent;
        }

        var queueName = LocalTransport.QueueName(uri);
        return AgentForLocalQueue(queueName);
    }

    public IReadOnlyList<Endpoint> ExclusiveListeners()
    {
        var allEndpoints = _options
            .Transports
            .AllEndpoints().ToArray();

        foreach (var endpoint in allEndpoints)
        {
            endpoint.Compile(_runtime);
        }

        return allEndpoints
            .Where(x => x is { IsListener: true, ListenerScope: ListenerScope.Exclusive })
            .ToList();
    }

    public Endpoint? EndpointByName(string endpointName)
    {
        return _options.Transports.AllEndpoints().ToArray().FirstOrDefault(x => x.EndpointName == endpointName);
    }

    public IListeningAgent? FindListeningAgent(Uri uri)
    {
        return _listeners.GetValueOrDefault(uri);
    }

    public IListeningAgent? FindListeningAgent(string endpointName)
    {
        return _listeners.Values.FirstOrDefault(x => x.Endpoint.EndpointName.EqualsIgnoreCase(endpointName));
    }

    public async Task StartListenersAsync()
    {
        var listeningEndpoints = _options.Transports.SelectMany(x => x.Endpoints())
            .Where(x => x is not LocalQueue)
            .Where(x => x.ShouldAutoStartAsListener(_options.Durability));

        foreach (var endpoint in listeningEndpoints)
        {
            await StartListenerAsync(endpoint, _runtime.Cancellation);
        }
    }

    public async Task StopListenerAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        if (_listeners.TryGetValue(endpoint.Uri, out var agent))
        {
            await agent.StopAndDrainAsync();
        }
    }

    public IListenerCircuit FindListenerCircuit(Uri address)
    {
        if (address.Scheme == TransportConstants.Local)
        {
            return (IListenerCircuit)GetOrBuildSendingAgent(address);
        }

        return (FindListeningAgent(address) ??
                FindListeningAgent(TransportConstants.Durable))!;
    }

    public async Task StartListenerAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        if (_listeners.TryGetValue(endpoint.Uri, out var agent))
        {
            if (agent.Status == ListeningStatus.Accepting) return;
            await agent.StartAsync();
            return;
        }

        endpoint.Compile(_runtime);
        agent = new ListeningAgent(endpoint, _runtime);
        await agent.StartAsync().ConfigureAwait(false);
        _listeners[agent.Uri] = agent;
    }

    public async Task StartListenerAsync(Endpoint endpoint, IListener listener, CancellationToken cancellationToken)
    {
        if (_listeners.TryGetValue(endpoint.Uri, out var agent))
        {
            if (agent.Status == ListeningStatus.Accepting) return;
            await agent.StartAsync();
            return;
        }

        endpoint.Compile(_runtime);
        agent = new ListeningAgent(endpoint, _runtime);
        await agent.StartAsync().ConfigureAwait(false);
        _listeners[agent.Uri] = agent;
    }

    public LocalQueue? LocalQueueForMessageType(Type messageType)
    {
        return _runtime.RoutingFor(messageType).Routes.OfType<MessageRoute>().FirstOrDefault(x => x.IsLocal)
            ?.Sender.Endpoint as LocalQueue;
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
                return new DurableSendingAgent(sender, _options.Durability,
                    _runtime.LoggerFactory.CreateLogger<DurableSendingAgent>(), _runtime.MessageTracking,
                    _runtime.Storage, endpoint);

            case EndpointMode.BufferedInMemory:
                return new BufferedSendingAgent(_runtime.LoggerFactory.CreateLogger<BufferedSendingAgent>(),
                    _runtime.MessageTracking, sender, _runtime.DurabilitySettings,
                    endpoint);

            case EndpointMode.Inline:
                return new InlineSendingAgent(_runtime.LoggerFactory.CreateLogger<InlineSendingAgent>(), sender,
                    endpoint, _runtime.MessageTracking,
                    _runtime.DurabilitySettings);
        }

        throw new InvalidOperationException();
    }

    private ISendingAgent buildSendingAgent(Uri uri, Action<Endpoint>? configureNewEndpoint)
    {
        var transport = _options.Transports.ForScheme(uri.Scheme);
        if (transport == null)
        {
            throw new UnknownTransportException(
                $"There is no known transport type that can send to the Destination {uri}");
        }

        var endpoint = transport.GetOrCreateEndpoint(uri);
        configureNewEndpoint?.Invoke(endpoint);

        endpoint.Compile(_runtime);

        endpoint.Runtime ??= _runtime; // This is important for serialization
        return endpoint.StartSending(_runtime, transport.ReplyEndpoint()?.Uri);
    }

    public async Task DrainAsync()
    {
        // Drain the listeners
        foreach (var listener in ActiveListeners())
        {
            try
            {
                await listener.StopAndDrainAsync();
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Failed to 'drain' outstanding messages in listener {Uri}", listener.Uri);
            }
        }

        foreach (var queue in _localSenders.Enumerate().Select(x => x.Value).OfType<ILocalQueue>())
        {
            try
            {
                await queue.DrainAsync();
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Failed to 'drain' outstanding messages in local sender {Queue}", queue);
            }
        }
    }

    internal void StoreSendingAgent(ISendingAgent agent)
    {
        _senders = _senders.AddOrUpdate(agent.Destination, agent);

        if (agent is DurableLocalQueue || agent is BufferedLocalQueue)
        {
            _localSenders = _localSenders.AddOrUpdate(LocalTransport.QueueName(agent.Destination), agent);
        }
    }

    public bool HasSender(Uri uri)
    {
        return _senders.Contains(uri);
    }
}