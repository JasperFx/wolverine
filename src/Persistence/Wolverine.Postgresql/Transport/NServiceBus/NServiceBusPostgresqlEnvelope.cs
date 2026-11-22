namespace Wolverine.Postgresql.Transport.NServiceBus;

/// <summary>
/// An envelope popped off an NServiceBus PostgreSQL queue table. Carries what a deferral needs to put the row
/// back where it was (GH-5775).
/// </summary>
internal class NServiceBusPostgresqlEnvelope : Envelope
{
    /// <summary>
    /// The row's <c>seq</c> when it was popped. A deferral writes the copy back with this value so the message
    /// keeps its FIFO place, the same place a rolled back NServiceBus receive leaves it in.
    /// </summary>
    public long? Seq { get; set; }
}
