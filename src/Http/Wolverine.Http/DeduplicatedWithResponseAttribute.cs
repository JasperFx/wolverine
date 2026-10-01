using JasperFx.CodeGeneration;
using Wolverine.Attributes;

namespace Wolverine.Http;

/// <summary>
/// GH-4742. Deduplicate this endpoint on an idempotency key and answer a repeat of the same request with the
/// first response: its status, body and <c>Location</c>. A key reused for a different request gets 422, and a
/// repeat while the first is still running gets 409.
///
/// <para>
/// Unlike <c>[Deduplicated]</c>, which refuses a repeat, this tells a client whose connection dropped
/// mid-request what it created. The two cannot be combined. The key is the caller's; the request is compared by
/// a hash of the bytes sent (method, path, query string and body), so a retry must resend the same bytes.
/// </para>
///
/// <para>
/// Requires <c>opts.Durability.EnableDeduplicatedResponses = true</c> and a PostgreSQL, SQL Server, MySQL or
/// SQLite message store. Without them the host logs a warning at startup and the endpoint throws at its first
/// claim. The response is recorded after the endpoint and any commit, before it is written and before cascaded
/// messages flush.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [DeduplicatedWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint)]
/// [WolverinePost("/orders")]
/// public static (OrderCreated, OrderPlaced) Post(PlaceOrder command) { }
/// </code>
/// </example>
// Methods only: on a class it would also reach any endpoint there that returns no resource.
[AttributeUsage(AttributeTargets.Method)]
public class DeduplicatedWithResponseAttribute : ModifyHttpChainAttribute
{
    /// <param name="scope">
    /// Who a key belongs to. Required: a stored response is replayed to anyone presenting the same key and
    /// request, so an unscoped key would let one caller read another's response.
    /// </param>
    public DeduplicatedWithResponseAttribute(DeduplicationScope scope)
    {
        Scope = scope;
    }

    public DeduplicationScope Scope { get; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Source" />
    public ValueSource Source { get; set; } = ValueSource.Anything;

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Key" />
    public string? Key { get; set; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Required" />
    public bool Required { get; set; } = true;

    /// <summary>Claim lifetime in seconds; 0 uses <see cref="DurabilitySettings.DeduplicationWindow" />.</summary>
    public int WindowInSeconds { get; set; }

    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        if (WindowInSeconds < 0)
        {
            throw new InvalidOperationException(
                $"[DeduplicatedWithResponse(WindowInSeconds = {WindowInSeconds})] on {chain.Description} must be zero (the host-wide window) or positive. See GH-4742");
        }

        // The rest is validated with the chain, once every attribute has been applied.
        chain.DeduplicatedWithResponse = new DeduplicatedWithResponseRequirement
        {
            Scope = Scope,
            Source = Source,
            Key = Key,
            Required = Required,
            Window = WindowInSeconds == 0 ? null : TimeSpan.FromSeconds(WindowInSeconds)
        };
    }
}
