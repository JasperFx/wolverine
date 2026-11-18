using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5572. One agent per saga store that keeps completed sagas (an <see cref="ISagaRetentionSweeper" />), so
///     exactly one node deletes the retained sagas of each store once they are past their retention period. No agents
///     at all unless some saga type is retained.
/// </summary>
internal class SagaRetentionAgentFamily : IStaticAgentFamily
{
    public const string SchemeName = "wolverine-saga-retention";

    private readonly IWolverineRuntime _runtime;
    private readonly object _gate = new();
    private Dictionary<Uri, SagaRetentionAgent>? _agents;

    public SagaRetentionAgentFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Scheme => SchemeName;

    /// <summary>
    ///     GH-5601: deleting retained sagas is storage housekeeping, like dead letter and handled envelope expiry, so it
    ///     goes to the nodes that hold the durability agents
    /// </summary>
    public NodeWorkloads RequiredWorkload => NodeWorkloads.Durability;

    public static Uri UriFor(string storeName)
    {
        return new Uri($"{SchemeName}:///{Uri.EscapeDataString(storeName)}");
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return ValueTask.FromResult<IReadOnlyList<Uri>>(agents().Keys.ToList());
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        if (agents().TryGetValue(uri, out var agent)) return ValueTask.FromResult<IAgent>(agent);

        throw new InvalidAgentException(
            $"'{uri}' is not a known saga retention agent. The valid values for this system are {agents().Keys.Select(x => x.ToString()).Join(", ")}");
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        if (agents().Count == 0) return new ValueTask();

        assignments.DistributeEvenlyWithBlueGreenSemantics(SchemeName);
        return new ValueTask();
    }

    internal IReadOnlyDictionary<Uri, SagaRetentionAgent> Agents => agents();

    private Dictionary<Uri, SagaRetentionAgent> agents()
    {
        if (_agents != null) return _agents;

        lock (_gate)
        {
            if (_agents != null) return _agents;

            var agents = new Dictionary<Uri, SagaRetentionAgent>();

            // Cheap check first, so an application that retains nothing never resolves its saga storages here
            if (anySagaTypeIsRetained())
            {
                var sweepers = _runtime.Services.GetServices<ISagaStoreDiagnostics>().OfType<ISagaRetentionSweeper>();
                foreach (var sweeper in sweepers)
                {
                    var retained = sweeper.SagaTypesForRetention()
                        .Distinct()
                        .Select(type => (type, retention: SagaRetention.For(type, _runtime.Options)))
                        .Where(x => x.retention.HasValue)
                        .Select(x => new RetainedSagaType(x.type, x.retention!.Value))
                        .ToArray();

                    if (retained.Length == 0) continue;

                    var name = sweeper.RetentionStoreName;
                    var uri = UriFor(name);
                    var suffix = 2;
                    while (agents.ContainsKey(uri)) uri = UriFor($"{name}-{suffix++}");

                    agents[uri] = new SagaRetentionAgent(uri, sweeper, retained, _runtime,
                        _runtime.LoggerFactory.CreateLogger<SagaRetentionAgent>());
                }
            }

            _agents = agents;
            return agents;
        }
    }

    private bool anySagaTypeIsRetained()
    {
        if (_runtime.Options.Durability.CompletedSagaRetention.HasValue) return true;

        return _runtime.Options.HandlerGraph.SagaChains()
            .Select(x => x.SagaType)
            .Distinct()
            .Any(x => x.HasAttribute<RetainWhenCompletedAttribute>());
    }
}

internal record RetainedSagaType(Type SagaType, TimeSpan Retention);

/// <summary>
///     GH-5572. Deletes the retained, completed sagas of one store once they are past their retention period, every
///     <see cref="DurabilitySettings.CompletedSagaSweepInterval" />
/// </summary>
internal class SagaRetentionAgent : IAgent
{
    private readonly ISagaRetentionSweeper _sweeper;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public SagaRetentionAgent(Uri uri, ISagaRetentionSweeper sweeper, IReadOnlyList<RetainedSagaType> sagaTypes,
        IWolverineRuntime runtime, ILogger logger)
    {
        Uri = uri;
        _sweeper = sweeper;
        SagaTypes = sagaTypes;
        _runtime = runtime;
        _logger = logger;
    }

    public Uri Uri { get; }

    public IReadOnlyList<RetainedSagaType> SagaTypes { get; }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public string Description =>
        $"Deletes completed sagas of {SagaTypes.Select(x => x.SagaType.Name).Join(", ")} from {_sweeper.RetentionStoreName} once they are past their retention period";

    /// <summary>
    ///     Delete every retained saga past its period, once
    /// </summary>
    /// <returns>The number of sagas deleted</returns>
    public async Task<long> SweepAsync(CancellationToken token)
    {
        long total = 0;
        foreach (var sagaType in SagaTypes)
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(sagaType.Retention);
            try
            {
                var deleted = await _sweeper.DeleteCompletedSagasAsync(sagaType.SagaType, cutoff, token);
                total += deleted;

                if (deleted > 0)
                {
                    _logger.LogInformation("Deleted {Count} completed sagas of type {SagaType} from {Store} that completed before {Cutoff}",
                        deleted, sagaType.SagaType.FullNameInCode(), _sweeper.RetentionStoreName, cutoff);
                }

                // GH-5665: the same cutoff deletes the message history of the sagas it deleted
                var history = await SagaHistory.SweepAsync(_runtime, sagaType.SagaType, cutoff, token);
                if (history > 0)
                {
                    _logger.LogInformation("Deleted {Count} saga history rows of completed sagas of type {SagaType} that completed before {Cutoff}",
                        history, sagaType.SagaType.FullNameInCode(), cutoff);
                }
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                _logger.LogError(e, "Failed to delete completed sagas of type {SagaType} from {Store}",
                    sagaType.SagaType.FullNameInCode(), _sweeper.RetentionStoreName);
            }
        }

        return total;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop != null) return Task.CompletedTask;

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(() => runAsync(token), CancellationToken.None);
        }

        Status = AgentStatus.Running;
        return Task.CompletedTask;
    }

    private async Task runAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await SweepAsync(token);

            try
            {
                await Task.Delay(_runtime.Options.Durability.CompletedSagaSweepInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellation != null)
        {
            await _cancellation.CancelAsync();
        }

        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _loop = null;
        _cancellation?.Dispose();
        _cancellation = null;
        Status = AgentStatus.Stopped;
    }
}
