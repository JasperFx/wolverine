namespace Wolverine.Http;

/// <summary>
/// GH-4742. Parts of an HTTP request folded into a <c>[DeduplicatedWithResponse]</c> key, so the same key from
/// another tenant, user or endpoint is a different claim. A stored response is replayed to anyone presenting the
/// same scoped key and request, so this decides who can be answered with whose response.
/// </summary>
[Flags]
public enum DeduplicationScope
{
    /// <summary>The key alone. Refused by <see cref="DeduplicatedWithResponseAttribute" />.</summary>
    None = 0,

    /// <summary>The detected tenant. Requires tenant id detection on the endpoint.</summary>
    Tenant = 1,

    /// <summary><c>ClaimsPrincipal.Identity.Name</c>. Anonymous requests all share the empty user.</summary>
    User = 2,

    /// <summary>The HTTP method and request path, route values included.</summary>
    Endpoint = 4
}
