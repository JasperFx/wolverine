using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// GH-5168. The agent family behind the short-cadence recurring schedules: one
/// <see cref="RecurringTimerAgent" /> per schedule whose <see cref="RecurringMessage.ExecutionPath" />
/// is <see cref="RecurringExecutionPath.Timer" />, at <c>wolverine-recurring-timer:///{name}</c>.
///
/// <para>
/// A static family, so every node advertises the timer schedules it knows as capabilities, and the
/// leader distributes them evenly with blue/green semantics: during a rolling deploy a timer
/// schedule only ever lands on a node whose release declares it. Warm-up nodes never build this
/// family (GH-4569), so they never run a timer schedule.
/// </para>
///
/// <para>
/// Where agents run without assignment the family is driven directly: Solo mode starts every agent
/// it knows locally, and a host with no message store starts them alongside the singular
/// recurring agent. Serverless and MediatorOnly refuse schedules at startup, exactly as before.
/// </para>
/// </summary>
internal class RecurringTimerAgentFamily : IStaticAgentFamily
{
    public const string SchemeName = "wolverine-recurring-timer";

    private readonly IWolverineRuntime _runtime;
    private readonly ConcurrentDictionary<string, RecurringTimerAgent> _agents = new();
    private readonly ConcurrentDictionary<string, bool> _locallyPaused = new();

    public RecurringTimerAgentFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Scheme => SchemeName;

    /// <summary>
    /// The agent URI for a timer schedule. The name is URL-encoded into the path (not the host, which
    /// a <see cref="System.Uri" /> lower-cases), so it round-trips exactly through
    /// <see cref="ScheduleNameFor" />.
    /// </summary>
    public static Uri UriFor(string scheduleName)
    {
        return new Uri($"{SchemeName}:///{Uri.EscapeDataString(scheduleName)}");
    }

    /// <summary>The schedule name a timer agent URI was built from, or null for any other URI.</summary>
    public static string? ScheduleNameFor(Uri agentUri)
    {
        if (!string.Equals(agentUri.Scheme, SchemeName, StringComparison.Ordinal)) return null;

        var path = agentUri.AbsolutePath.TrimStart('/');
        return path.Length == 0 ? null : Uri.UnescapeDataString(path);
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var uris = _runtime.Options.Schedules.TimerSchedules().Select(x => UriFor(x.Name)).ToList();
        return ValueTask.FromResult<IReadOnlyList<Uri>>(uris);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        return ValueTask.FromResult<IAgent>(AgentFor(uri));
    }

    /// <summary>
    /// The one agent instance per timer schedule, cached for the life of the runtime like
    /// <see cref="ExclusiveListenerFamily" />'s. Throws <see cref="InvalidAgentException" /> for a URI
    /// that names no timer schedule of this application.
    /// </summary>
    internal RecurringTimerAgent AgentFor(Uri uri)
    {
        var name = ScheduleNameFor(uri);
        var message = name == null ? null : _runtime.Options.Schedules.FindByName(name);

        if (message == null || message.ExecutionPath != RecurringExecutionPath.Timer)
        {
            var known = _runtime.Options.Schedules.TimerSchedules().Select(x => UriFor(x.Name).ToString());
            throw new InvalidAgentException(
                $"'{uri}' is not a known recurring timer schedule. The valid values for this system are {known.Join(", ")}");
        }

        return _agents.GetOrAdd(message.Name,
            _ => new RecurringTimerAgent(message, _runtime,
                _runtime.LoggerFactory.CreateLogger<RecurringTimerAgent>()));
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        if (!_runtime.Options.Schedules.TimerSchedules().Any()) return new ValueTask();

        assignments.DistributeEvenlyWithBlueGreenSemantics(SchemeName);
        return new ValueTask();
    }

    /// <summary>
    /// The timer agents of a host with no message store, where nothing assigns agents: started
    /// directly, like the singular recurring agent on such a host.
    /// </summary>
    internal async Task<IReadOnlyList<RecurringTimerAgent>> StartAllLocallyAsync(CancellationToken token)
    {
        var started = new List<RecurringTimerAgent>();
        foreach (var uri in await AllKnownAgentsAsync())
        {
            var agent = AgentFor(uri);
            if (IsLocallyPaused(agent.Message.Name)) continue;

            await agent.StartAsync(token);
            started.Add(agent);
        }

        return started;
    }

    internal async Task StopAllLocallyAsync()
    {
        foreach (var agent in _agents.Values)
        {
            if (agent.Status == JasperFx.AgentStatus.Running)
            {
                await agent.StopAsync(CancellationToken.None);
            }
        }
    }

    // The pause channel of a host with no message store, where there are no agent restrictions to
    // carry it. In-process only and lost on restart, matching the singular agent's in-memory pause.
    internal void MarkLocallyPaused(string name) => _locallyPaused[name] = true;

    internal void MarkLocallyResumed(string name) => _locallyPaused.TryRemove(name, out _);

    internal bool IsLocallyPaused(string name) => _locallyPaused.ContainsKey(name);
}
