using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime;

public class UnknownWolverineNodeException : Exception
{
    public UnknownWolverineNodeException(Guid nodeId) : base($"Attempting to make a remote call to an unknown node {nodeId}. This could be from that node having gone offline before the message was processed.")
    {
    }
}

public partial class WolverineRuntime : IAgentRuntime
{
    private readonly List<IAgent> _activeAgents = new();
    private BufferedLocalQueue _systemQueue;

    internal Timer? AgentTimer { get; private set; }

    public Task StartLocallyAsync(Uri agentUri)
    {
        if (_agents == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return _agents.StartAgentAsync(agentUri);
    }

    public Task StopLocallyAsync(Uri agentUri)
    {
        if (_agents == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return _agents.StopAgentAsync(agentUri);
    }

    public async Task InvokeAsync(Guid nodeId, IAgentCommand command)
    {
        if (Tracker.Self.Id == nodeId)
        {
            await new MessageBus(this).InvokeAsync(command, Cancellation);
        }
        else if (Tracker.Nodes.TryGetValue(nodeId, out var node))
        {
            var endpoint = node.ControlUri;
            await new MessageBus(this).EndpointFor(endpoint).InvokeAsync(command, Cancellation, 10.Seconds());
        }
        else
        {
            throw new UnknownWolverineNodeException(nodeId);
        }
    }

    public async Task<T> InvokeAsync<T>(Guid nodeId, IAgentCommand command) where T : class
    {
        if (Tracker.Self.Id == nodeId)
        {
            return await new MessageBus(this).InvokeAsync<T>(command, Cancellation);
        }
        if (Tracker.Nodes.TryGetValue(nodeId, out var node))
        {
            var endpoint = node.ControlUri;
            return await new MessageBus(this).EndpointFor(endpoint).InvokeAsync<T>(command, Cancellation, 10.Seconds());
        }

        throw new UnknownWolverineNodeException(nodeId);
    }

    public Uri[] AllRunningAgentUris()
    {
        return _agents.AllRunningAgentUris();
    }

    public IAgentRuntime Agents => this;

    private async Task startAgentsAsync()
    {
        if (Storage is NullMessageStore)
        {
            return;
        }

        _systemQueue = (BufferedLocalQueue)Endpoints.GetOrBuildSendingAgent(TransportConstants.SystemQueueUri);

        _agents = new NodeAgentController(this, Tracker, Storage.Nodes, _container.GetAllInstances<IAgentController>(),
            LoggerFactory.CreateLogger<NodeAgentController>(), Options.Durability.Cancellation);

        _agents.AddHandlers(this);

        AgentTimer = new Timer(fireHealthCheck, null, Options.Durability.FirstHealthCheckExecution,
            Options.Durability.HealthCheckPollingTime);

        var bus = new MessageBus(this);
        await bus.InvokeAsync(new StartLocalAgentProcessing(Options), Cancellation);
    }

    private void fireHealthCheck(object? state)
    {
        try
        {
            _systemQueue.EnqueueDirectly(new Envelope(new CheckAgentHealth())
            {
                Serializer = Options.DefaultSerializer,
                Destination = TransportConstants.SystemQueueUri
            });
        }
        catch (Exception e)
        {
            // No earthly idea why this would happen, but real life and all
            Logger.LogError(e, "Error trying to enqueue a CheckAgentHealth message");
        }
    }

    private async Task teardownAgentsAsync()
    {
        if (AgentTimer != null)
        {
            try
            {
                await AgentTimer.DisposeAsync();
            }
            catch (Exception)
            {
                // Don't really care, make this stop
            }
        }

        if (_agents != null)
        {
            var bus = new MessageBus(this);
            await _agents.StopAsync(bus);
        }
    }
}