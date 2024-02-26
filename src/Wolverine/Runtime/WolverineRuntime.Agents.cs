using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime;

public class UnknownWolverineNodeException : Exception
{
    public UnknownWolverineNodeException(Guid nodeId) : base(
        $"Attempting to make a remote call to an unknown node {nodeId}. This could be from that node having gone offline before the message was processed.")
    {
    }
}

public partial class WolverineRuntime : IAgentRuntime
{
    internal Timer? AgentTimer { get; private set; }

    public NodeAgentController? NodeController { get; private set; }

    public Task StartLocallyAsync(Uri agentUri)
    {
        if (NodeController == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return NodeController.StartAgentAsync(agentUri);
    }

    public Task StopLocallyAsync(Uri agentUri)
    {
        if (NodeController == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return NodeController.StopAgentAsync(agentUri);
    }

    public async Task InvokeAsync(Guid nodeId, IAgentCommand command)
    {
        if (Tracker.Self!.Id == nodeId)
        {
            await new MessageBus(this).InvokeAsync(command, Cancellation);
        }
        else if (Tracker.Nodes.TryGetValue(nodeId, out var node))
        {
            var endpoint = node.ControlUri;
            await new MessageBus(this).EndpointFor(endpoint!).InvokeAsync(command, Cancellation, 30.Seconds());
        }
        else
        {
            throw new UnknownWolverineNodeException(nodeId);
        }
    }

    public async Task<T> InvokeAsync<T>(Guid nodeId, IAgentCommand command) where T : class
    {
        if (Tracker.Self!.Id == nodeId)
        {
            return await new MessageBus(this).InvokeAsync<T>(command, Cancellation);
        }

        if (Tracker.Nodes.TryGetValue(nodeId, out var node))
        {
            var endpoint = node.ControlUri;
            return await new MessageBus(this).EndpointFor(endpoint!).InvokeAsync<T>(command, Cancellation, 10.Seconds());
        }

        throw new UnknownWolverineNodeException(nodeId);
    }

    public Uri[] AllRunningAgentUris()
    {
        return NodeController!.AllRunningAgentUris();
    }

    public async Task KickstartHealthDetectionAsync()
    {
        await new MessageBus(this).InvokeAsync(new CheckAgentHealth(), Options.Durability.Cancellation);
    }

    public IAgentRuntime Agents => this;

    private async Task startAgentsAsync()
    {
        if (Storage is NullMessageStore)
        {
            return;
        }

        switch (Options.Durability.Mode)
        {
            case DurabilityMode.Balanced:
                startDurableScheduledJobs();
                startNodeAgentController();
                await startNodeAgentWorkflowAsync();
                break;
            
            
            case DurabilityMode.Solo:
                startDurableScheduledJobs();
                startNodeAgentController();
                await NodeController!.StartSoloModeAsync();
                break;
            
            
            case DurabilityMode.Serverless:
            case DurabilityMode.MediatorOnly:
                break;
        }


    }

    private void startDurableScheduledJobs()
    {
        DurableScheduledJobs = Storage.StartScheduledJobs(this);
    }

    internal BufferedLocalQueue? SystemQueue { get; private set; }

    internal IAgent? DurableScheduledJobs { get; private set; }

    private void startNodeAgentController()
    {
        INodeAgentPersistence nodePersistence = Options.Durability.Mode == DurabilityMode.Balanced
            ? Storage.Nodes
            : new NullNodeAgentPersistence();
        
        NodeController = new NodeAgentController(this, Tracker, nodePersistence, _container.GetAllInstances<IAgentFamily>(),
            LoggerFactory.CreateLogger<NodeAgentController>(), Options.Durability.Cancellation);

        NodeController.AddHandlers(this);
    }

    private async Task startNodeAgentWorkflowAsync()
    {
        SystemQueue = (BufferedLocalQueue)Endpoints.GetOrBuildSendingAgent(TransportConstants.SystemQueueUri);

        var startingTime = new Random().Next(0, 2000);

        var bus = new MessageBus(this);
        await bus.InvokeAsync(new StartLocalAgentProcessing(Options), Cancellation);
        
        AgentTimer = new Timer(fireHealthCheck, null, startingTime.Milliseconds(),
            Options.Durability.HealthCheckPollingTime);
    }
    
    private void fireHealthCheck(object? state)
    {
        try
        {
            SystemQueue!.EnqueueDirectly(new Envelope(new CheckAgentHealth())
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

        if (NodeController != null)
        {
            var bus = new MessageBus(this);
            await NodeController.StopAsync(bus);
        }
    }

    /// <summary>
    ///     STRICTLY FOR TESTING!!!!
    /// </summary>
    /// <param name="lastHeartbeatTime"></param>
    internal async Task DisableAgentsAsync(DateTimeOffset lastHeartbeatTime)
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

        if (NodeController != null)
        {
            await NodeController.DisableAgentsAsync();
            await _persistence.Value.Nodes.OverwriteHealthCheckTimeAsync(Options.UniqueNodeId, lastHeartbeatTime);
        }

        NodeController = null;
    }
}