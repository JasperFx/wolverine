using JasperFx.Core;

namespace Wolverine;

/// <summary>
/// The id generators behind <see cref="EnvelopeIdGeneration.NewId"/> and
/// <see cref="EnvelopeIdGeneration.NewIdSqlServerOrder"/>, both from JasperFx's <see cref="SequentialGuid"/>.
/// </summary>
/// <remarks>
/// <see cref="SequentialGuid"/> keeps NewId's layout (a 100 ns timestamp, a 6-byte worker id and a 16-bit
/// sequence), but its worker id is 6 random bytes drawn once per process rather than the machine's network
/// identity, so two processes on one machine (or two containers that share a network identity) do not draw the
/// same ids. It writes the sequence big-endian in both orders, so more than 256 ids in one 100 ns tick still sort
/// in the order they were drawn (GH-5626). Both orders come from one shared generator, so ids of either order are
/// unique against each other.
/// </remarks>
internal static class EnvelopeIds
{
    /// <summary>
    /// Ordered by time the way .NET and PostgreSQL compare GUIDs
    /// </summary>
    public static Guid NextSequentialGuid() => SequentialGuid.NextPostgresOrder();

    /// <summary>
    /// Ordered by time the way SQL Server compares a <c>uniqueidentifier</c>
    /// </summary>
    public static Guid NextGuid() => SequentialGuid.NextSqlServerOrder();
}
