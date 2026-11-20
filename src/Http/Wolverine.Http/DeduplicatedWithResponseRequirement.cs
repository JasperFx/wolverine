using System.Diagnostics.CodeAnalysis;
using Wolverine.Attributes;
using Wolverine.Persistence;

namespace Wolverine.Http;

/// <summary>
/// GH-4742. How a <c>[DeduplicatedWithResponse]</c> endpoint resolves its key, and who the key belongs to. See
/// <see cref="DeduplicatedWithResponseAttribute" />.
/// </summary>
public sealed class DeduplicatedWithResponseRequirement
{
    public DeduplicatedWithResponseRequirement()
    {
    }

    /// <summary>
    /// GH-5617. A requirement whose scope may be left to <see cref="WolverineHttpOptions.DeduplicateWithResponse" />:
    /// <c>[DeduplicatedWithResponse]</c> with no scope.
    /// </summary>
    [SetsRequiredMembers]
    internal DeduplicatedWithResponseRequirement(DeduplicationScope? scope)
    {
        _scope = scope;
    }

    /// <summary>Who a key belongs to. Must not be <see cref="DeduplicationScope.None" />.</summary>
    public required DeduplicationScope Scope
    {
        get => _scope ?? DeduplicationScope.None;
        init => _scope = value;
    }

    private readonly DeduplicationScope? _scope;

    /// <summary>
    /// GH-5617. No scope was given, so it comes from <see cref="WolverineHttpOptions.DeduplicateWithResponse" />.
    /// Validated once the policies have run, not when the chain is built.
    /// </summary>
    internal bool InheritsScope => _scope == null;

    /// <summary>
    /// Where the key comes from. <see cref="ValueSource.Anything" /> means the
    /// <see cref="DeduplicationRequirement.DefaultHeaderName" /> request header, or the header named <see cref="Key" />.
    /// </summary>
    public ValueSource Source
    {
        get => _source ?? ValueSource.Anything;
        init => _source = value;
    }

    private readonly ValueSource? _source;

    /// <summary>Whether <see cref="Source" /> was chosen here. See <see cref="InheritFrom" />.</summary>
    internal ValueSource? ExplicitSource
    {
        get => _source;
        init => _source = value;
    }

    /// <summary>The header, member, route or query key holding the key.</summary>
    public string? Key { get; init; }

    /// <summary>Must a key be present? Default is <see langword="true" />, as for <c>[Deduplicated]</c>.</summary>
    public bool Required
    {
        get => _required ?? true;
        init => _required = value;
    }

    private readonly bool? _required;

    /// <summary>Whether <see cref="Required" /> was chosen here. See <see cref="InheritFrom" />.</summary>
    internal bool? ExplicitRequired
    {
        get => _required;
        init => _required = value;
    }

    /// <summary>Claim lifetime, or null for <see cref="DurabilitySettings.DeduplicationWindow" />.</summary>
    public TimeSpan? Window { get; init; }

    /// <summary>
    /// GH-5160. Write the claim, with its response, inside the endpoint's own transaction, where its store can. Today that is
    /// an endpoint committing through a Marten session on a single-database store, with the PostgreSQL message store
    /// in the same database. A rollback, or a crash before the commit, then leaves no claim, and a commit leaves it
    /// answered.
    /// </summary>
    /// <remarks>
    /// Off by default, because it changes what a concurrent repeat does. Nothing is claimed up front, so a repeat
    /// that arrives while the first is running runs the endpoint too, instead of getting 409, and is answered from
    /// the first's claim when its own commit fails. Work outside the transaction runs for both. An endpoint that
    /// cannot take this path keeps claim-and-release, and the host logs a warning naming it.
    ///
    /// <para>
    /// Unset, an endpoint takes <see cref="WolverineHttpOptions.DefaultClaimInTransaction" />; this reports false
    /// either way. See <see cref="ExplicitClaimInTransaction" />.
    /// </para>
    /// </remarks>
    public bool ClaimInTransaction
    {
        get => _claimInTransaction ?? false;
        init => _claimInTransaction = value;
    }

    private readonly bool? _claimInTransaction;

    /// <summary>
    /// Whether <see cref="ClaimInTransaction" /> was actually chosen here, as opposed to falling through to
    /// <see cref="WolverineHttpOptions.DefaultClaimInTransaction" />. The same split as
    /// <c>DeduplicationRequirement.ExplicitDuplicateStatusCode</c>.
    /// </summary>
    internal bool? ExplicitClaimInTransaction
    {
        get => _claimInTransaction;
        init => _claimInTransaction = value;
    }

    /// <summary>
    /// Store the response body with the claim. Default is <see langword="true" />. Set it to <see langword="false" /> on
    /// an endpoint whose response holds a one-time secret, such as a newly created API key: the body is then never
    /// serialized or written, and a repeat is answered with the first response's status and <c>Location</c> and no
    /// body.
    /// </summary>
    public bool StoreBody
    {
        get => _storeBody ?? true;
        init => _storeBody = value;
    }

    private readonly bool? _storeBody;

    /// <summary>Whether <see cref="StoreBody" /> was chosen here. See <see cref="InheritFrom" />.</summary>
    internal bool? ExplicitStoreBody
    {
        get => _storeBody;
        init => _storeBody = value;
    }

    /// <summary>
    /// GH-5617. This endpoint's own settings, with everything it left unset taken from <paramref name="defaults" />,
    /// the requirement a <see cref="WolverineHttpOptions.DeduplicateWithResponse" /> policy would give it. The
    /// attributes run when the chain is built and the policies after, so this is the <c>??=</c> that keeps an
    /// endpoint's own choices. <see cref="Source" /> and <see cref="Key" /> are one setting: an endpoint that names
    /// either keeps both.
    /// </summary>
    internal DeduplicatedWithResponseRequirement InheritFrom(DeduplicatedWithResponseRequirement defaults)
    {
        var ownKey = _source.HasValue || Key != null;

        return new DeduplicatedWithResponseRequirement(_scope ?? defaults._scope)
        {
            ExplicitSource = ownKey ? _source : defaults._source,
            Key = ownKey ? Key : defaults.Key,
            ExplicitRequired = _required ?? defaults._required,
            Window = Window ?? defaults.Window,
            ExplicitClaimInTransaction = _claimInTransaction ?? defaults._claimInTransaction,
            ExplicitStoreBody = _storeBody ?? defaults._storeBody
        };
    }

    /// <summary>
    /// The header that marks a replayed response unless
    /// <see cref="WolverineHttpOptions.DeduplicatedReplayHeaderName" /> names another.
    /// </summary>
    public const string DefaultReplayHeaderName = "Idempotent-Replayed";

    internal string KeyName => Key ?? DeduplicationRequirement.DefaultHeaderName;

    public override string ToString()
    {
        var description = Source == ValueSource.Anything
            ? $"DeduplicatedWithResponse ('{KeyName}' header, Scope = {Scope}, Required = {Required}"
            : $"DeduplicatedWithResponse by {Source} '{Key}' (Scope = {Scope}, Required = {Required}";

        if (Window.HasValue) description += $", Window = {Window.Value}";
        if (_claimInTransaction.HasValue) description += $", ClaimInTransaction = {_claimInTransaction.Value}";
        if (!StoreBody) description += ", StoreBody = False";

        return description + ")";
    }
}
