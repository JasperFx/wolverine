using JasperFx.Core;
using Wolverine.Configuration;

namespace Wolverine.Runtime.Agents;

internal class ExclusiveListenerAgent : IAgent
{
    private readonly Endpoint _endpoint;
    private readonly IWolverineRuntime _runtime;

    public ExclusiveListenerAgent(Endpoint endpoint, IWolverineRuntime runtime)
    {
        _endpoint = endpoint;
        _runtime = runtime;

        Uri = new Uri($"{ExclusiveListenerFamily.SchemeName}://{_endpoint.EndpointName}");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _runtime.Endpoints.StartListenerAsync(_endpoint, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runtime.Endpoints.StopListenerAsync(_endpoint, cancellationToken);
        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; set; }
    
    public AgentStatus Status { get; set; } = AgentStatus.Started;
}

internal class ExclusiveListenerFamily : IStaticAgentFamily
{
    private readonly IWolverineRuntime _runtime;
    internal const string SchemeName = "wolverine-listener";

    private readonly WolverineOptions _options;
    private readonly Dictionary<Uri,ExclusiveListenerAgent> _agents;

    public ExclusiveListenerFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;

        _agents = _runtime.Endpoints.ExclusiveListeners().Select(e => new ExclusiveListenerAgent(e, runtime))
            .ToDictionary(e => e.Uri);
    }

    public string Scheme { get; set; } = SchemeName;
    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var uris = _agents.Keys.ToList();
        return ValueTask.FromResult<IReadOnlyList<Uri>>(uris);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        if (_agents.TryGetValue(uri, out var agent))
        {
            return ValueTask.FromResult<IAgent>(agent);
        }

        throw new InvalidAgentException(
            $"'{uri}' is not a known exclusive listener Uri. The valid values for this system are {_agents.Keys.Select(x => x.ToString()).Join(", ")}");
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(SchemeName);
        return new ValueTask();
    }
}