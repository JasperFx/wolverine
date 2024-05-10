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
    private bool _agentsAreDisabled;
    private Task? _healthCheckLoop;

    private CancellationTokenSource _agentCancellation;

    public NodeAgentController? NodeController { get; private set; }

    public Task StartLocallyAsync(Uri agentUri)
    {
        if (Cancellation.IsCancellationRequested || _agentsAreDisabled) return Task.CompletedTask;

        if (NodeController == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return NodeController.StartAgentAsync(agentUri);
    }

    public Task StopLocallyAsync(Uri agentUri)
    {
        if (Cancellation.IsCancellationRequested || _agentsAreDisabled) return Task.CompletedTask;

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
            await new MessageBus(this).InvokeAsync(command, _agentCancellation.Token);
        }
        else if (Tracker.Nodes.TryGetValue(nodeId, out var node))
        {
            var endpoint = node.ControlUri;
            await new MessageBus(this).EndpointFor(endpoint!).InvokeAsync(command, _agentCancellation.Token, 60.Seconds());
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
            return await new MessageBus(this).InvokeAsync<T>(command, _agentCancellation.Token, 30.Seconds());
        }

        if (Tracker.Nodes.TryGetValue(nodeId, out var node))
        {
            var endpoint = node.ControlUri;
            return await new MessageBus(this).EndpointFor(endpoint!).InvokeAsync<T>(command, _agentCancellation.Token, 60.Seconds());
        }

        throw new UnknownWolverineNodeException(nodeId);
    }

    public Uri[] AllRunningAgentUris()
    {
        return NodeController!.AllRunningAgentUris();
    }

    public async Task KickstartHealthDetectionAsync()
    {
        if (NodeController != null)
        {
            await new MessageBus(this).InvokeAsync(new CheckAgentHealth(), Options.Durability.Cancellation);
        }
    }

    public Task<AgentCommands> DoHealthChecksAsync()
    {
        if (NodeController != null) return NodeController.DoHealthChecksAsync();
        return Task.FromResult(AgentCommands.Empty);
    }

    public void DisableHealthChecks()
    {
        _agentCancellation.Cancel();

        if (_healthCheckLoop == null) return;

        if (NodeController != null)
        {
            NodeController.CancelHeartbeatChecking();
        }
    }

    public Task<AgentCommands> VerifyAssignmentsAsync()
    {
        if (NodeController != null) return NodeController.VerifyAssignmentsAsync();
        return Task.FromResult(AgentCommands.Empty);
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

    internal IAgent? DurableScheduledJobs { get; private set; }

    private void startNodeAgentController()
    {
        INodeAgentPersistence nodePersistence = Options.Durability.Mode == DurabilityMode.Balanced
            ? Storage.Nodes
            : new NullNodeAgentPersistence();

        NodeController = new NodeAgentController(this, Tracker, nodePersistence, _container.GetAllInstances<IAgentFamily>(),
            LoggerFactory.CreateLogger<NodeAgentController>(), _agentCancellation.Token);

        NodeController.AddHandlers(this);
    }

    private async Task startNodeAgentWorkflowAsync()
    {
        if (NodeController != null)
        {
            var commands = await NodeController.StartLocalAgentProcessingAsync(Options);
            foreach (var command in commands)
            {
                await new MessageBus(this).PublishAsync(command);
            }
        }

        _healthCheckLoop = Task.Run(executeHealthChecks, Cancellation);
    }

    private async Task executeHealthChecks()
    {
        await Task.Delay(Options.Durability.FirstHealthCheckExecution, Cancellation);

        while (!Cancellation.IsCancellationRequested)
        {
            await Task.Delay(Options.Durability.HealthCheckPollingTime, Cancellation);

            if (NodeController != null)
            {
                try
                {

                    var commands = await NodeController.DoHealthChecksAsync();
                    var bus = new MessageBus(this);

                    // TODO -- try to parallelize this later!!!
                    while (commands.Any())
                    {
                        var command = commands.Pop();
                        var additional = await bus.InvokeAsync<AgentCommands>(command, Cancellation);
                        if (additional != null)
                        {
                            commands.AddRange(additional);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Nothing here
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error trying to perform agent health checks");
                }
            }
        }
    }

    private async Task teardownAgentsAsync()
    {
        if (_healthCheckLoop != null)
        {
            _healthCheckLoop.SafeDispose();
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
        _agentsAreDisabled = true;

        if (_healthCheckLoop != null)
        {
            _healthCheckLoop.SafeDispose();
        }

        if (NodeController != null)
        {
            await NodeController.DisableAgentsAsync();
            await _persistence.Value.Nodes.OverwriteHealthCheckTimeAsync(Options.UniqueNodeId, lastHeartbeatTime);
        }

        NodeController = null;
    }

    public Task<AgentCommands> AssumeLeadershipAsync(Guid? currentLeaderId)
    {
        if (NodeController != null)
        {
            return NodeController.AssumeLeadershipAsync(currentLeaderId);
        }

        return Task.FromResult(AgentCommands.Empty);
    }

    public Task<AgentCommands> ApplyNodeEvent(NodeEvent nodeEvent)
    {
        if (NodeController != null) return NodeController.ApplyNodeEventAsync(nodeEvent);

        return Task.FromResult(AgentCommands.Empty);
    }

    public Task<AgentCommands> StartLocalAgentProcessingAsync()
    {
        if (NodeController != null)
        {
            return NodeController.StartLocalAgentProcessingAsync(Options);
        }

        return Task.FromResult(AgentCommands.Empty);
    }
}