using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.AI;
using Wolverine.Transports.Local;

namespace Wolverine.AI;

/// <summary>
/// Configuration for the durable LLM callout tier, supplied through
/// <c>WolverineOptions.AddLlmCallouts(...)</c>.
/// </summary>
public class LlmCalloutOptions
{
    private readonly List<Action<LocalQueueConfiguration>> _queueConfigurations = new();

    /// <summary>
    /// How many callouts may be in flight at once on the callout queue. This is the back pressure knob
    /// — it caps concurrent calls to the model provider, not the rate at which callouts are published.
    /// Defaults to 5.
    /// </summary>
    public int MaximumParallelCallouts { get; set; } = 5;

    /// <summary>
    /// Should the callout queue be durable? True by default, which is the whole point: a callout
    /// published from a handler is then enrolled in the outbox with that handler's write, so a callout
    /// cannot fire for a transaction that did not commit, and a callout cannot be lost to a process
    /// restart between the commit and the model call.
    ///
    /// <para>
    /// Set false only where message persistence is not configured at all. The queue then falls back to
    /// buffered in memory, and callouts are lost on shutdown like any other buffered message.
    /// </para>
    /// </summary>
    public bool DurableQueue { get; set; } = true;

    /// <summary>
    /// How long any one callout may take before it is cancelled and retried. Defaults to 2 minutes,
    /// which is generous for a one shot completion and deliberately shorter than most provider level
    /// defaults, so a hung request surfaces as a Wolverine retry rather than as a stuck handler.
    /// </summary>
    public TimeSpan Timeout { get; set; } = 2.Minutes();

    /// <summary>
    /// Cooldown schedule applied to transient callout failures — anything that is not an
    /// <see cref="LlmCalloutException" /> or an <see cref="LlmBudgetExceededException" />. After the
    /// last cooldown the callout is dead lettered. Set to an empty array to opt out and configure the
    /// queue's error handling yourself.
    /// </summary>
    public TimeSpan[] RetryCooldowns { get; set; } = [1.Seconds(), 5.Seconds(), 15.Seconds()];

    /// <summary>
    /// Model id used for any callout that does not name its own with
    /// <see cref="LlmCallout.UsingModel" />. Null leaves the choice to the registered
    /// <see cref="IChatClient" />.
    /// </summary>
    public string? DefaultModelId { get; set; }

    /// <summary>
    /// System prompt used for any callout that does not carry its own. Sent as
    /// <see cref="ChatOptions.Instructions" />.
    /// </summary>
    public string? DefaultSystemPrompt { get; set; }

    /// <summary>
    /// Spend guardrails enforced by middleware on the callout queue.
    /// </summary>
    public LlmBudget Budget { get; } = new();

    /// <summary>
    /// Serializer options used both to build the JSON schema handed to the model and to deserialize its
    /// answer. Defaults to <see cref="AIJsonUtilities.DefaultOptions" />, which is what the rest of
    /// Microsoft.Extensions.AI uses.
    ///
    /// <para>
    /// A trimmed or AOT application assigns options whose <c>TypeInfoResolver</c> is a source generated
    /// <c>JsonSerializerContext</c> over its response types. Both the schema generation and the
    /// deserialization run off that resolver, so neither needs reflection. See the AOT guide.
    /// </para>
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = AIJsonUtilities.DefaultOptions;

    /// <summary>
    /// Response types registered by <see cref="RegisterResponseType{TResponse}" />, keyed by the
    /// identifier a callout carries on the wire.
    /// </summary>
    public IReadOnlyDictionary<string, Type> RegisteredResponseTypes => _registeredResponseTypes;

    private readonly Dictionary<string, Type> _registeredResponseTypes = new();

    /// <summary>
    /// Declare a response type up front, so the executor can turn a persisted callout's identifier back
    /// into a <see cref="Type" /> without reflection.
    ///
    /// <para>
    /// Optional for an ordinary application — the executor falls back to the message type registry and
    /// then to <see cref="Type.GetType(string)" />. <b>Required</b> for a trimmed or AOT one, where
    /// neither fallback can be trusted: register every response type here, and put those same types in
    /// the <c>JsonSerializerContext</c> assigned to <see cref="JsonSerializerOptions" />.
    /// </para>
    /// </summary>
    public LlmCalloutOptions RegisterResponseType<TResponse>()
    {
        _registeredResponseTypes[LlmCallout.IdentifierFor(typeof(TResponse))] = typeof(TResponse);
        return this;
    }

    /// <summary>
    /// Claim each callout's <see cref="LlmCallout.DeduplicationId" /> before executing it, so a callout
    /// published twice with the same logical identity only reaches the model once. Off by default, and
    /// it additionally requires <c>opts.Durability.EnableMessageDeduplication</c> and a persistence
    /// provider that supports it.
    ///
    /// <para>
    /// Callouts without an id are still executed — a mixed stream is the normal case, since only the
    /// republish prone sources (a projection's <c>RaiseSideEffects</c>, say) have a natural key.
    /// </para>
    /// </summary>
    public bool DeduplicateCallouts { get; set; }

    /// <summary>
    /// Further configuration of the local queue callouts execute on, beyond
    /// <see cref="MaximumParallelCallouts" /> and <see cref="DurableQueue" />.
    /// </summary>
    public LlmCalloutOptions ConfigureQueue(Action<LocalQueueConfiguration> configure)
    {
        _queueConfigurations.Add(configure);
        return this;
    }

    internal void ApplyQueueConfiguration(LocalQueueConfiguration queue)
    {
        foreach (var configure in _queueConfigurations) configure(queue);
    }
}

/// <summary>
/// Spend guardrails for LLM callouts, enforced by middleware on the callout queue. Both limits dead
/// letter rather than retry: a callout that is over budget is over budget on every attempt, and
/// retrying it is the runaway spend the budget exists to stop.
/// </summary>
public class LlmBudget
{
    /// <summary>
    /// Rejects any callout whose composed user message exceeds this many characters. The cheap poison
    /// prompt guard — a runaway context assembled from an unbounded collection is the usual way a
    /// single callout costs a hundred times what it should. Null disables the check.
    /// </summary>
    public int? MaximumPromptCharacters { get; set; }

    /// <summary>
    /// Rejects callouts once total token usage reported by the provider over the trailing
    /// <see cref="Window" /> reaches this figure. Null disables the check.
    ///
    /// <para>
    /// This is a per process ledger, not a cluster wide one. On a fleet of N nodes the effective ceiling
    /// is N times this number — set it accordingly, and treat it as a circuit breaker against a runaway
    /// loop rather than as billing enforcement.
    /// </para>
    /// </summary>
    public long? MaximumTokensPerWindow { get; set; }

    /// <summary>
    /// The trailing window <see cref="MaximumTokensPerWindow" /> is measured over. Defaults to one
    /// minute.
    /// </summary>
    public TimeSpan Window { get; set; } = 1.Minutes();

    internal bool IsEnabled => MaximumPromptCharacters.HasValue || MaximumTokensPerWindow.HasValue;
}
