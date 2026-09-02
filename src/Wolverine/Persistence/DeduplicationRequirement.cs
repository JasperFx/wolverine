using Wolverine.Attributes;

namespace Wolverine.Persistence;

/// <summary>
/// GH-4180. Describes how one chain — a message handler, an HTTP endpoint, or a gRPC method —
/// resolves its <b>logical</b> deduplication id, and what should happen when that id has already
/// been seen.
///
/// <para>
/// The id itself is application-defined and application-supplied. Wolverine does not derive one:
/// a framework-derived key is either the delivery id (which <see cref="Envelope.Id" /> already is,
/// and which cannot express "the same intent, published twice") or a content hash, which silently
/// changes meaning the moment an irrelevant field is added to the message type.
/// </para>
/// </summary>
public sealed class DeduplicationRequirement
{
    /// <summary>
    /// Where the logical id comes from. <see cref="ValueSource.Anything" /> means "use this chain
    /// type's natural default", which is <see cref="Envelope.DeduplicationId" /> for message
    /// handlers, the <see cref="DefaultHeaderName" /> request header for HTTP endpoints, and the
    /// same name in request metadata for gRPC.
    /// </summary>
    public ValueSource Source { get; init; } = ValueSource.Anything;

    /// <summary>
    /// The header name, message/request member name, or route/query key holding the logical id.
    /// Ignored when <see cref="Source" /> is <see cref="ValueSource.Anything" />.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Must a logical id be present? Default is <see langword="true" />.
    ///
    /// <para>
    /// This defaults to strict because the lenient reading is the dangerous one. A chain that asked
    /// for deduplication and then quietly processed every keyless message would report itself as
    /// protected while providing nothing, and the failure is invisible: the traffic succeeds, the
    /// duplicates run, and no log line distinguishes it from a working configuration. Set
    /// <see langword="false" /> only when a mixed stream is genuinely expected — some publishers
    /// supply an id and some do not — and you want the ones that do to be protected.
    /// </para>
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>
    /// HTTP only: the status code returned when the logical id has already been claimed. Default is
    /// 409 Conflict with a <c>ProblemDetails</c> body.
    ///
    /// <para>
    /// Set to 200 or 204 when a replayed request is benign for this endpoint and the caller should see
    /// success rather than a conflict — a create that is safe to repeat, say. Any other value is
    /// written as a problem document, so the caller gets a machine-readable reason rather than a bare
    /// code.
    /// </para>
    ///
    /// <para>
    /// This lives on the chain-agnostic requirement rather than an HTTP-only subclass because it is a
    /// single nullable hint, and a parallel type hierarchy for one integer would cost more than it
    /// explains. Message handler and gRPC chains ignore it.
    /// </para>
    /// </summary>
    public int DuplicateStatusCode
    {
        get => _duplicateStatusCode ?? DefaultDuplicateStatusCode;
        init => _duplicateStatusCode = value;
    }

    private readonly int? _duplicateStatusCode;

    /// <summary>
    /// Whether a status code was actually chosen here, as opposed to falling through to the default.
    /// <see cref="DuplicateStatusCode" /> cannot answer that on its own -- it reports 409 either way --
    /// and Wolverine.HTTP needs the distinction so that
    /// <c>WolverineHttpOptions.DefaultDuplicateStatusCode</c> applies to endpoints that did not state a
    /// preference without overriding the ones that did.
    /// </summary>
    internal int? ExplicitDuplicateStatusCode
    {
        get => _duplicateStatusCode;
        init => _duplicateStatusCode = value;
    }

    /// <summary>
    /// The built-in default for an already-claimed id: 409 Conflict. An application-wide override for
    /// HTTP endpoints lives on <c>WolverineHttpOptions.DefaultDuplicateStatusCode</c>.
    /// </summary>
    public const int DefaultDuplicateStatusCode = 409;

    /// <summary>
    /// The conventional header/metadata name carrying a logical idempotency key, matching the IETF
    /// draft that Stripe, Adyen and others already follow. Used as the default for HTTP and gRPC so
    /// that a caller who already sends this header gets deduplication with no extra configuration.
    /// </summary>
    public const string DefaultHeaderName = "Idempotency-Key";

    public override string ToString()
        => Source == ValueSource.Anything
            ? $"Deduplicated (chain default, Required = {Required})"
            : $"Deduplicated by {Source} '{Key}' (Required = {Required})";
}
