namespace Wolverine.Persistence.Durability;

public class DuplicateIncomingEnvelopeException : Exception
{
    public DuplicateIncomingEnvelopeException(Envelope envelope) : base(
        $"Duplicate incoming envelope with message id {envelope.Id} at {envelope.Destination}")
    {
        Duplicates = new[] { envelope };
    }

    /// <summary>
    /// When the source can pinpoint each duplicate, <see cref="Duplicates"/> contains
    /// only the actual duplicates. When the source cannot tell (e.g. a transactional
    /// batch insert that was rolled back as a unit), the entire batch is reported
    /// here as "potentially duplicates" and callers should treat the list as
    /// informational rather than authoritative.
    /// </summary>
    public DuplicateIncomingEnvelopeException(IReadOnlyList<Envelope> duplicates) : base(
        Format(duplicates))
    {
        Duplicates = duplicates;
    }

    private static string Format(IReadOnlyList<Envelope> duplicates)
    {
        ArgumentNullException.ThrowIfNull(duplicates);
        if (duplicates.Count == 0)
            throw new ArgumentException("At least one envelope is required", nameof(duplicates));

        return $"Duplicate incoming envelopes detected ({duplicates.Count} envelope(s))";
    }

    public IReadOnlyList<Envelope> Duplicates { get; }
}
