namespace Wolverine.Http;

/// <summary>
///     Directs Wolverine to write an empty <b>204</b> instead of the default <b>404</b> when this endpoint's
///     response body is null, denoting "the Url is correct, but there is no body." Valid on an endpoint method or
///     on an endpoint class, where it applies to every endpoint method in that class. A method level declaration
///     wins over a class level one.
/// </summary>
/// <remarks>
///     <para>
///         This is only about the response <i>body</i>. It has no effect on what happens when a required entity
///         cannot be loaded -- use <c>[Entity(OnMissing = OnMissing.EmptyContentWith204)]</c>, or the
///         <c>WolverineOptions.EntityDefaults.OnMissing</c> global default, for that.
///     </para>
///     <para>
///         Only legal on GET and QUERY (RFC 10008) endpoints. Those are the safe, side effect free reads where an
///         empty answer is a benign outcome. On any other HTTP method a 204 in place of a resource would quietly
///         turn a failed command into an apparent success on the client, so Wolverine fails fast at bootstrapping
///         time instead.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class NoContentIfMissingAttribute : Attribute;

/// <summary>
///     Directs Wolverine to write an empty <b>404</b> when this endpoint's response body is null. This is already
///     Wolverine's default, so this attribute is only useful to opt a single endpoint or endpoint class back out of
///     an application wide <c>WolverineHttpOptions.OnMissingResponseBody = OnMissingResponseBody.NoContent204</c>,
///     or to opt one method out of a class level <see cref="NoContentIfMissingAttribute" />.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class NotFoundIfMissingAttribute : Attribute;
