namespace Wolverine.Runtime.Serialization;

/// <summary>
/// Per-host caps applied by <see cref="EnvelopeSerializer"/> when reading
/// inbound envelopes off the wire. The caps prevent attacker-controlled
/// 32-bit length fields from driving multi-gigabyte allocations.
/// </summary>
public sealed record EnvelopeReaderLimits(
    int MaxBatchSize,
    int MaxDataSize,
    int MaxHeaderCount)
{
    public static EnvelopeReaderLimits Default { get; } = new(
        MaxBatchSize: 1_000,
        MaxDataSize: 4 * 1024 * 1024,
        MaxHeaderCount: 128);
}
