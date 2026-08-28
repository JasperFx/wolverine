namespace Wolverine.Persistence;

/// <summary>
/// GH-4180. Thrown when a chain requires a logical deduplication id
/// (<see cref="DeduplicationRequirement.Required" />) and the incoming message, request, or call did
/// not carry one.
///
/// <para>
/// Deliberately NOT discarded by a built-in error policy, unlike
/// <see cref="Durability.DuplicateIncomingEnvelopeException" />. A duplicate means the work is
/// already done and discarding is correct; a missing id means the work has not been done and never
/// will be, so it needs to be as loud as any other unhandled failure.
/// </para>
/// </summary>
public class MissingDeduplicationIdException : Exception
{
    public MissingDeduplicationIdException(string description) : base(
        $"A logical deduplication id is required but was not supplied for {description}. Either have the caller supply one, or set Required = false on the [Deduplicated] attribute to allow unkeyed traffic through unchecked. See GH-4180")
    {
    }
}
