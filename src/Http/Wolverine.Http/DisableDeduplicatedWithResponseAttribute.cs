namespace Wolverine.Http;

/// <summary>
/// GH-5617. Leave this endpoint, or every endpoint in this class, out of
/// <see cref="WolverineHttpOptions.DeduplicateWithResponse" />. It gets no <c>[DeduplicatedWithResponse]</c> from
/// there, and no refusal codes in its OpenAPI metadata. A <c>[DeduplicatedWithResponse]</c> on the endpoint itself
/// still applies, and must then name its own scope.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class DisableDeduplicatedWithResponseAttribute : Attribute;
