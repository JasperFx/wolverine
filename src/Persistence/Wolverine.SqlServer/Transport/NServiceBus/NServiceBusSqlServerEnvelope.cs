namespace Wolverine.SqlServer.Transport.NServiceBus;

/// <summary>
/// An envelope popped off an NServiceBus SQL Server queue table (GH-5775).
/// </summary>
internal class NServiceBusSqlServerEnvelope : Envelope
{
    /// <summary>
    /// Set once a deferral has tried to put this message back into the queue table. Any later attempt may follow
    /// one that committed while reporting a failure, so it must not insert a second copy.
    /// </summary>
    public bool ReinsertAttempted { get; set; }
}
