using JasperFx.CodeGeneration;
using Wolverine.Attributes;

namespace Wolverine.Http;

/// <summary>
/// GH-4742. Deduplicate this endpoint on an idempotency key and answer a repeat of the same request with the
/// first response: its status, body and <c>Location</c>, or without the body when <see cref="StoreBody" /> is
/// false. A key reused for a different request gets 422, and a repeat while the first is still running gets 409,
/// unless <see cref="ClaimInTransaction" /> is set.
///
/// <para>
/// Unlike <c>[Deduplicated]</c>, which refuses a repeat, this tells a client whose connection dropped
/// mid-request what it created. The two cannot be combined. The key is the caller's; the request is compared by
/// a hash of the bytes sent (method, path, query string and body), so a retry must resend the same bytes. A
/// multipart body is compared by its parts, so a retry may use a new boundary, and a form-encoded body by its fields.
/// The antiforgery token field is left out of a form.
/// </para>
///
/// <para>
/// Requires <c>opts.Durability.EnableDeduplicatedResponses = true</c> and a PostgreSQL, SQL Server, MySQL or
/// SQLite message store. Without them the host logs a warning at startup and the endpoint throws at its first
/// claim. The response is recorded after the endpoint and any commit, before it is written and before cascaded
/// messages flush, unless <see cref="ClaimInTransaction" /> writes it inside the commit instead.
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
        _scope = scope;
    }

    /// <summary>
    /// GH-5617. Take the scope, and anything else this attribute leaves unset, from
    /// <see cref="WolverineHttpOptions.DeduplicateWithResponse" />, so an endpoint can change one setting, such as
    /// <c>[DeduplicatedWithResponse(StoreBody = false)]</c>, without restating the rest. An endpoint that no
    /// <c>DeduplicateWithResponse()</c> call covers is refused at startup, as it has no scope.
    /// </summary>
    public DeduplicatedWithResponseAttribute()
    {
    }

    /// <summary>
    /// Who a key belongs to, or <see cref="DeduplicationScope.None" /> when it comes from
    /// <see cref="WolverineHttpOptions.DeduplicateWithResponse" />.
    /// </summary>
    public DeduplicationScope Scope => _scope ?? DeduplicationScope.None;

    private readonly DeduplicationScope? _scope;

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Source" />
    public ValueSource Source
    {
        get => _source ?? ValueSource.Anything;
        set => _source = value;
    }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Key" />
    public string? Key { get; set; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Required" />
    public bool Required
    {
        get => _required ?? true;
        set => _required = value;
    }

    /// <summary>
    /// Claim lifetime in seconds; 0 takes the window from <see cref="WolverineHttpOptions.DeduplicateWithResponse" />,
    /// else <see cref="DurabilitySettings.DeduplicationWindow" />.
    /// </summary>
    public int WindowInSeconds { get; set; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.ClaimInTransaction" />
    /// <remarks>
    /// Left unset, the endpoint takes <see cref="WolverineHttpOptions.DefaultClaimInTransaction" />. Set it either way
    /// to override that default for this endpoint.
    /// </remarks>
    public bool ClaimInTransaction
    {
        get => _claimInTransaction ?? false;
        set => _claimInTransaction = value;
    }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.StoreBody" />
    public bool StoreBody
    {
        get => _storeBody ?? true;
        set => _storeBody = value;
    }

    // Null until somebody actually writes ClaimInTransaction on the attribute, so an endpoint that said nothing can
    // inherit WolverineHttpOptions.DefaultClaimInTransaction while one that said false keeps false. GH-5617: the same
    // for Source, Required and StoreBody, which an endpoint may take from WolverineHttpOptions.DeduplicateWithResponse()
    private bool? _claimInTransaction;
    private ValueSource? _source;
    private bool? _required;
    private bool? _storeBody;

    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        if (WindowInSeconds < 0)
        {
            throw new InvalidOperationException(
                $"[DeduplicatedWithResponse(WindowInSeconds = {WindowInSeconds})] on {chain.Description} must be zero (the host-wide window) or positive. See GH-4742");
        }

        // The rest is validated with the chain, once every attribute has been applied.
        chain.DeduplicatedWithResponse = new DeduplicatedWithResponseRequirement(_scope)
        {
            ExplicitSource = _source,
            Key = Key,
            ExplicitRequired = _required,
            Window = WindowInSeconds == 0 ? null : TimeSpan.FromSeconds(WindowInSeconds),
            ExplicitClaimInTransaction = _claimInTransaction,
            ExplicitStoreBody = _storeBody
        };
    }
}
