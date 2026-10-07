using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Persistence;

namespace Wolverine.Attributes;

/// <summary>
/// GH-4180. Opt this message handler, HTTP endpoint, or gRPC method into <b>logical</b> message
/// deduplication: Wolverine resolves an application-supplied id and refuses to execute a second
/// time for the same id inside <see cref="DurabilitySettings.DeduplicationWindow" />, or inside
/// <see cref="WindowInSeconds" /> when that is set.
///
/// <para>
/// This is a different question from Wolverine's built-in idempotency, which keys on
/// <see cref="Envelope.Id" /> and therefore identifies one <i>delivery</i>. Logical deduplication
/// identifies one <i>intent</i> — "rebuild projection X for tonight's 03:00 run" — and so survives
/// an operator double-clicking, a console republishing, or an agent pre-publishing an occurrence
/// that later arrives again on its own.
/// </para>
///
/// <para>
/// Requires <c>opts.Durability.MessageDeduplicationMode</c>, which provisions the backing
/// storage. Without it the chain throws at its first message or request, rather than silently letting
/// every duplicate through, and a message handler also logs a warning at startup.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // Message handler: uses Envelope.DeduplicationId, set by the publisher through DeliveryOptions
/// [Deduplicated]
/// public static void Handle(RebuildProjection command) { }
///
/// // HTTP endpoint: uses the conventional "Idempotency-Key" request header
/// [Deduplicated]
/// [WolverinePost("/schedules/{scheduleId}/occurrences")]
/// public static async Task&lt;IResult&gt; Post(ScheduleOccurrence body) { }
///
/// // Derive the id from the message or request body instead
/// [Deduplicated(ValueSource.InputMember, nameof(ScheduleOccurrence.OccurrenceKey))]
/// public static void Handle(ScheduleOccurrence command) { }
///
/// // Claim the id for ten minutes rather than the host-wide window
/// [Deduplicated(WindowInSeconds = 600)]
/// public static void Handle(DeviceStateChanged message) { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class DeduplicatedAttribute : ModifyChainAttribute
{
    public DeduplicatedAttribute()
    {
    }

    /// <param name="key">
    /// The header name — or, with an explicit <see cref="Source" />, the member / route / query key —
    /// holding the logical id.
    /// </param>
    public DeduplicatedAttribute(string key)
    {
        Key = key;
        Source = ValueSource.Header;
    }

    public DeduplicatedAttribute(ValueSource source, string key)
    {
        Source = source;
        Key = key;
    }

    /// <summary>
    /// Where the logical id comes from. Defaults to this chain type's natural source:
    /// <see cref="Envelope.DeduplicationId" /> for message handlers, and the
    /// <see cref="DeduplicationRequirement.DefaultHeaderName" /> header for HTTP and gRPC.
    /// </summary>
    public ValueSource Source { get; set; } = ValueSource.Anything;

    /// <summary>
    /// The header / member / route key holding the logical id. Ignored when <see cref="Source" /> is
    /// left at <see cref="ValueSource.Anything" />.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Must the id be present? Defaults to <see langword="true" />; see
    /// <see cref="DeduplicationRequirement.Required" /> for why the strict reading is the safe default.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// HTTP endpoints only: the status code for an already-claimed id. Default 409 Conflict; use 200 or
    /// 204 where a replayed request is benign. See <see cref="DeduplicationRequirement.DuplicateStatusCode" />.
    /// </summary>
    public int DuplicateStatusCode
    {
        get => _duplicateStatusCode ?? DeduplicationRequirement.DefaultDuplicateStatusCode;
        set => _duplicateStatusCode = value;
    }

    // Null until somebody actually writes DuplicateStatusCode on the attribute. Kept so an endpoint
    // that said nothing can inherit WolverineHttpOptions.DefaultDuplicateStatusCode while one that
    // asked for 409 explicitly keeps it even when the application default is something else.
    private int? _duplicateStatusCode;

    /// <summary>Claim lifetime in seconds; 0 uses <see cref="DurabilitySettings.DeduplicationWindow" />.</summary>
    public int WindowInSeconds { get; set; }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        // Records the intent only. The frames are woven later, from ApplyDeduplication(), because the
        // shape of what gets woven depends on IChain.IsTransactional -- and that is not settled until
        // the persistence providers' policies have run, well after attributes are applied.
        chain.Deduplication = new DeduplicationRequirement
        {
            Source = Source,
            Key = Key,
            Required = Required,
            ExplicitDuplicateStatusCode = _duplicateStatusCode,
            Window = WindowFor(chain.Description)
        };
    }

    /// <summary>
    /// <see cref="WindowInSeconds" /> as the requirement carries it: null for the host-wide window. Shared with
    /// Wolverine.Grpc, which reads this attribute per RPC method rather than through <see cref="Modify" />.
    /// </summary>
    internal TimeSpan? WindowFor(string chainDescription)
    {
        if (WindowInSeconds < 0)
        {
            throw new InvalidOperationException(
                $"[Deduplicated(WindowInSeconds = {WindowInSeconds})] on {chainDescription} must be zero (the host-wide window) or positive");
        }

        return WindowInSeconds == 0 ? null : TimeSpan.FromSeconds(WindowInSeconds);
    }
}
