using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Util;

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
                await ad.DisposeAsync();
            }
            else if (sender is IDisposable d)
            {
                d.Dispose();
            }
        }

        foreach (var value in _listeners.Values) await value.DisposeAsync();
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

    public Endpoint? EndpointByName(string endpointName)
    {
        return _options.Transports.AllEndpoints().ToArray().FirstOrDefault(x => x.EndpointName == endpointName);
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
        return _listeners.Values.FirstOrDefault(x => x.Endpoint.EndpointName.EqualsIgnoreCase(endpointName));
    }

    public async Task StartListenersAsync()
    {
        var listeningEndpoints = _options.Transports.SelectMany(x => x.Endpoints())
            .Where(x => x.IsListener).Where(x => x is not LocalQueue);

        foreach (var endpoint in listeningEndpoints)
        {
            endpoint.Compile(_runtime);
            var agent = new ListeningAgent(endpoint, _runtime);
            await agent.StartAsync().ConfigureAwait(false);
            _listeners[agent.Uri] = agent;
        }
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
                    _runtime.Storage, endpoint);

            case EndpointMode.BufferedInMemory:
                return new BufferedSendingAgent(_runtime.Logger, _runtime.MessageLogger, sender, _runtime.Advanced,
                    endpoint);

            case EndpointMode.Inline:
                return new InlineSendingAgent(_runtime.Logger, sender, endpoint, _runtime.MessageLogger,
                    _runtime.Advanced);
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
}