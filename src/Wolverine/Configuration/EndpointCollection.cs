using ImTools;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
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
    /// Collect a point-in-time health snapshot of all active endpoints.
    /// </summary>
    IReadOnlyList<EndpointHealthSnapshot> CollectEndpointHealth();

    /// <summary>
    /// Endpoints where the message listener should only be active on a single endpoint
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<Endpoint> ExclusiveListeners();
    
    /// <summary>
    /// Endpoints where the message listener should only be active on the leader node
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<Endpoint> LeaderPinnedListeners();

    Task StartListenerAsync(Endpoint endpoint, CancellationToken cancellationToken);
    Task StopListenerAsync(Endpoint endpoint, CancellationToken cancellationToken);

    IListenerCircuit? FindListenerCircuit(Uri address);

    /// <summary>
    /// Is the listening endpoint at this address scoped to a single node in the cluster -- i.e.
    /// <see cref="ListenerScope.Exclusive"/> or <see cref="ListenerScope.PinnedToLeader"/> rather than
    /// <see cref="ListenerScope.CompetingConsumers"/>? Inbox recovery for these endpoints is owned by the
    /// node hosting the listener itself, *not* by the database's durability agent. See GH-3590.
    /// </summary>
    bool IsSingleNodeListener(Uri address)
    {
        return EndpointFor(address) is { IsSingleNodeListener: true };
    }
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

            if (sender is ISenderRequiresCallback senderRequiringCallback)
            {
                // GH-4073. This used to be a single `&&` with the agent test, so an agent that could not carry the
                // callback simply fell through and the sender was left unregistered. That is not a benign no-op:
                // BatchedSender -- the only ISenderRequiresCallback in the codebase -- throws "This sender has not
                // been registered." from inside its own block on the FIRST outgoing batch, on a worker thread the
                // caller never observes. The send just never happens, and the symptom that surfaces is a test or a
                // subscriber timing out, far away from the endpoint that was actually misconfigured.
                //
                // The pairing rule is total: ISenderCallback is implemented only by SendingAgent, the base of the
                // buffered and durable agents. So a callback-requiring sender is compatible with exactly the
                // BufferedInMemory and Durable modes, and a transport that builds one for an Inline or NativeAck
                // endpoint has a bug in its CreateSender gate. Fail the bootstrap loudly and name the fix.
                if (agent is not ISenderCallback callbackAgent)
                {
                    throw new InvalidOperationException(
                        $"Endpoint {endpoint.Uri} is in EndpointMode.{endpoint.Mode}, which sends through {agent.GetType().Name}, " +
                        $"but its transport built a {sender.GetType().Name} that requires an {nameof(ISenderCallback)} to deliver anything. " +
                        $"The transport's CreateSender is most likely gating its inline sender on 'Mode == EndpointMode.Inline'; " +
                        $"it should gate on the {nameof(Endpoint)}.{nameof(Endpoint.SendsInline)} predicate instead, which also covers " +
                        $"EndpointMode.{nameof(EndpointMode.NativeAck)}.");
                }

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

    public IReadOnlyList<EndpointHealthSnapshot> CollectEndpointHealth()
    {
        var snapshots = new List<EndpointHealthSnapshot>();

        foreach (var listener in _listeners.Values)
        {
            var loopHealth = receiveLoopHealthOf(listener);
            snapshots.Add(new EndpointHealthSnapshot(
                Uri: listener.Uri,
                EndpointName: listener.Endpoint.EndpointName,
                Direction: EndpointDirection.Listening,
                Status: listener.Status.ToString(),
                QueueCount: listener.QueueCount,
                LastQueueActivityAt: listener.LastQueueActivityAt,
                LastMessageSentAt: null,
                SenderLatched: false,
                // GH-4199: only report the buffering ceiling on the modes that actually enforce it. Inline and
                // NativeAck build no BackPressureAgent, so filling this in there hands an operator headroom
                // that does not exist -- invisible until GH-4186 made QueueCount real for those two modes.
                BufferLimit: listener.Endpoint.ShouldEnforceBackPressure()
                    ? listener.Endpoint.BufferingLimits?.Maximum
                    : null,
                ConnectionState: connectionStateOf(listener),
                ReceiveLoopStatus: loopHealth?.ReceiveLoopStatus ?? ReceiveLoopStatus.Unknown,
                LastReceiveLoopActivityAt: loopHealth?.LastReceiveLoopActivityAt,
                InFlightLimit: listener.Endpoint.InFlightLimit,
                LaneCount: listener.LaneDepth?.LaneCount,
                BusiestLaneCount: listener.LaneDepth?.BusiestLaneCount,
                ExemptLaneCount: listener.LaneDepth?.ExemptLaneCount));
        }

        foreach (var sender in _senders.Enumerate().Select(x => x.Value))
        {
            snapshots.Add(new EndpointHealthSnapshot(
                Uri: sender.Destination,
                EndpointName: sender.Endpoint?.EndpointName ?? "unknown",
                Direction: EndpointDirection.Sending,
                Status: sender.Latched ? "Latched" : "Active",
                QueueCount: 0,
                LastQueueActivityAt: null,
                LastMessageSentAt: sender.LastMessageSentAt,
                SenderLatched: sender.Latched,
                BufferLimit: null,
                ConnectionState: connectionStateOf(sender)));
        }

        return snapshots;
    }

    // Resolve the background receive-loop health for a listener. The agent itself may report it, otherwise reach
    // through to the IListener it owns. Listeners with no managed loop (push transports, local queues) report null.
    private static IReportReceiveLoopHealth? receiveLoopHealthOf(IListeningAgent agent)
    {
        if (agent is IReportReceiveLoopHealth reporter)
        {
            return reporter;
        }

        if (agent is ListeningAgent { Listener: IReportReceiveLoopHealth listenerReporter })
        {
            return listenerReporter;
        }

        return null;
    }

    // Resolve the underlying transport channel/connection state for a listener. The agent itself may report it
    // (IReportConnectionState), otherwise reach through to the IListener it owns. Transports without a connection
    // notion fall through to Unknown.
    private static TransportConnectionState connectionStateOf(IListeningAgent agent)
    {
        if (agent is IReportConnectionState reporter)
        {
            return reporter.ConnectionState;
        }

        if (agent is ListeningAgent { Listener: IReportConnectionState listenerReporter })
        {
            return listenerReporter.ConnectionState;
        }

        return TransportConnectionState.Unknown;
    }

    // Resolve the underlying transport channel/connection state for a sending agent. The agent itself may report it,
    // otherwise reach through to the ISender it wraps (SendingAgent / InlineSendingAgent both expose Sender).
    private static TransportConnectionState connectionStateOf(ISendingAgent agent)
    {
        if (agent is IReportConnectionState reporter)
        {
            return reporter.ConnectionState;
        }

        var sender = agent switch
        {
            SendingAgent sa => sa.Sender,
            InlineSendingAgent ia => ia.Sender,
            _ => null
        };

        return sender is IReportConnectionState senderReporter
            ? senderReporter.ConnectionState
            : TransportConnectionState.Unknown;
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
            .Where(x => x is { IsListener: true, ListenerScope: ListenerScope.Exclusive } and not LocalQueue)
            .ToList();
    }

    public IReadOnlyList<Endpoint> LeaderPinnedListeners()
    {
        var allEndpoints = _options
            .Transports
            .AllEndpoints().ToArray();

        foreach (var endpoint in allEndpoints)
        {
            endpoint.Compile(_runtime);
        }

        return allEndpoints
            .Where(x => x is { IsListener: true, ListenerScope: ListenerScope.PinnedToLeader })
            .ToList();
    }

    public Endpoint? EndpointByName(string endpointName)
    {
        return _options.Transports.AllEndpoints().FirstOrDefault(x => x.EndpointName == endpointName);
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
        if (_options.DisableAllExternalListeners) return;
        
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

    private ImHashMap<Uri, bool> _singleNodeListeners = ImHashMap<Uri, bool>.Empty;

    public bool IsSingleNodeListener(Uri address)
    {
        // Cached because this is asked on every durability agent recovery pass, once per distinct
        // received_at destination, and EndpointFor() is a linear scan across every transport.
        if (_singleNodeListeners.TryFind(address, out var isSingleNode))
        {
            return isSingleNode;
        }

        isSingleNode = EndpointFor(address) is { IsSingleNodeListener: true };
        _singleNodeListeners = _singleNodeListeners.AddOrUpdate(address, isSingleNode);

        return isSingleNode;
    }

    public IListenerCircuit? FindListenerCircuit(Uri address)
    {
        if (address.Scheme == TransportConstants.Local)
        {
            return (IListenerCircuit)GetOrBuildSendingAgent(address);
        }

        var agent = FindListeningAgent(address);
        if (agent != null)
        {
            return agent;
        }

        // GH-4296. The address on an inbox row is whatever listener stamped it, and that is not always an
        // address anything is registered under. A database transport that is multi-tenanted by database
        // registers ONE listening agent for the logical queue and then receives through a per-database
        // listener that stamps "postgresql://queue/database" -- so every orphaned row from a dead node is
        // addressed to a listener that does not exist by that name, and inbox recovery walked straight past
        // it forever. Ask the transport to translate before giving up.
        var logicalAddress = resolveListenerAddress(address);
        if (logicalAddress != null)
        {
            agent = FindListeningAgent(logicalAddress);
            if (agent != null)
            {
                return agent;
            }
        }

        return FindListeningAgent(TransportConstants.Durable);
    }

    private ImHashMap<Uri, Uri?> _resolvedListenerAddresses = ImHashMap<Uri, Uri?>.Empty;

    // Cached for the same reason IsSingleNodeListener is: this is asked on every durability agent recovery
    // pass, once per distinct received_at value.
    private Uri? resolveListenerAddress(Uri address)
    {
        if (_resolvedListenerAddresses.TryFind(address, out var resolved))
        {
            return resolved;
        }

        try
        {
            resolved = _options.Transports.ForScheme(address.Scheme)?.TryResolveListenerAddress(address);
        }
        catch (Exception e)
        {
            _runtime.Logger.LogDebug(e, "Unable to resolve a listening address for inbox address {Address}",
                address);
            resolved = null;
        }

        _resolvedListenerAddresses = _resolvedListenerAddresses.AddOrUpdate(address, resolved);

        return resolved;
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

        // Resolve combined sending failure policies (endpoint-specific takes priority over global)
        var sendingPolicies = resolveSendingFailurePolicies(endpoint);

        switch (endpoint.Mode)
        {
            case EndpointMode.Durable:
                var outbox = _runtime.Stores.HasAnyAncillaryStores()
                    ? new DelegatingMessageOutbox(_runtime.Storage.Outbox, _runtime.Stores)
                    : _runtime.Storage.Outbox;

                return new DurableSendingAgent(sender, _options.Durability,
                    _runtime.LoggerFactory.CreateLogger<DurableSendingAgent>(), _runtime.MessageTrackingFor(endpoint),
                    outbox, endpoint, _runtime, sendingPolicies);

            case EndpointMode.BufferedInMemory:
                return new BufferedSendingAgent(_runtime.LoggerFactory.CreateLogger<BufferedSendingAgent>(),
                    _runtime.MessageTrackingFor(endpoint), sender, _runtime.DurabilitySettings,
                    endpoint, _runtime, sendingPolicies);

            case EndpointMode.Inline:
                return new InlineSendingAgent(_runtime.LoggerFactory.CreateLogger<InlineSendingAgent>(), sender,
                    endpoint, _runtime.MessageTrackingFor(endpoint),
                    _runtime.DurabilitySettings, _runtime, sendingPolicies);

            case EndpointMode.NativeAck:
                // GH-3708 / GH-3709. Mode is a single property governing BOTH directions, so an endpoint that listens
                // with native acks and is also used for replies or sending arrives here.
                //
                // This deliberately maps to the INLINE sending agent rather than the buffered one, reversing the
                // original GH-3708 choice. NativeAck is a listening optimization -- nobody selects it for its sending
                // characteristics -- so the outgoing side should be the safe option rather than the fast one.
                //
                // The concrete failure that forced this: GlobalPartitionedInterceptor.TryReRouteAsync re-publishes a
                // message and then acks the SOURCE delivery. BufferedSendingAgent.storeAndForwardAsync posts to an
                // in-memory Block and returns before the envelope reaches the transport, so the source was being
                // settled while the only copy lived in this process's memory -- a crash in between lost the message
                // outright, with no redelivery because the source was already acked. Under the durable topology that
                // hop was safe because the re-publish hit the outbox first. InlineSendingAgent posts to a RetryBlock
                // that runs the send rather than queueing it, so the ack follows the send.
                //
                // Note this narrows the window rather than eliminating it: Wolverine publishes with RabbitMQ
                // publisher confirms disabled by default, so an inline send awaits the frame being written, not the
                // broker acknowledging it. Enabling confirms on these endpoints is the remaining step.
                return new InlineSendingAgent(_runtime.LoggerFactory.CreateLogger<InlineSendingAgent>(), sender,
                    endpoint, _runtime.MessageTrackingFor(endpoint),
                    _runtime.DurabilitySettings, _runtime, sendingPolicies);
        }

        throw new InvalidOperationException(
            $"Unknown {nameof(EndpointMode)} '{endpoint.Mode}' for the sending endpoint at {endpoint.Uri}");
    }

    private SendingFailurePolicies? resolveSendingFailurePolicies(Endpoint endpoint)
    {
        var globalPolicies = _options.SendingFailure;
        var endpointPolicies = endpoint.SendingFailure;

        if (endpointPolicies != null && globalPolicies.HasAnyRules)
        {
            return endpointPolicies.CombineWith(globalPolicies);
        }

        if (endpointPolicies != null)
        {
            return endpointPolicies;
        }

        if (globalPolicies.HasAnyRules)
        {
            return globalPolicies;
        }

        return null;
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

    /// <summary>
    /// Immediately latch all receivers to stop picking up new messages from their internal queues.
    /// During normal shutdown this is no longer called globally; instead each ListeningAgent
    /// latches its own receiver after stopping the listener (see StopAndDrainAsync).
    /// Kept to not break public api compatability.
    /// </summary>
    public void LatchAllReceivers()
    {
        foreach (var listener in _listeners.Values)
        {
            listener.LatchReceiver();
        }

        foreach (var queue in _localSenders.Enumerate().Select(x => x.Value).OfType<DurableLocalQueue>())
        {
            queue.LatchReceiver();
        }
    }

    public async Task DrainAsync()
    {
        await Task.WhenAll(ActiveListeners().ToArray().Select(async listener =>
        {
            try
            {
                await listener.StopAndDrainAsync();
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Failed to 'drain' outstanding messages in listener {Uri}", listener.Uri);
            }
        }));

        await Task.WhenAll(_localSenders.Enumerate().Select(x => x.Value).OfType<ILocalQueue>().Select(async queue =>
        {
            try
            {
                await queue.DrainAsync();
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Failed to 'drain' outstanding messages in local sender {Queue}", queue);
            }
        }));
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

    internal async Task RemoveSendingAgentAsync(Uri destination)
    {
        ISendingAgent? agent = null;
        lock (_channelLock)
        {
            if (!_senders.TryFind(destination, out agent)) return;
            _senders = _senders.Remove(destination);
        }

        // Endpoint.Agent is a second, independent handle on the agent we are about to dispose, and
        // DestinationEndpoint and MessageRoute both read it. Leaving it set handed callers a disposed
        // agent long after this collection had forgotten it. See GH-3955.
        if (ReferenceEquals(agent.Endpoint.Agent, agent))
        {
            agent.Endpoint.Agent = null;
        }

        if (agent is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
        }
    }
}
