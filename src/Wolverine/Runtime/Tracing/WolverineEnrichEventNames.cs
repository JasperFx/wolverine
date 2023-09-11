namespace Wolverine.Runtime.Tracing;

public static class WolverineEnrichEventNames
{
    public const string StartSendEnvelope = WolverineActivitySource.SendEnvelopeActivityName;
    public const string StartReceivingEnvelope = WolverineActivitySource.ReceiveEnvelopeActivityName;
    public const string StartExecutingEnvelope = WolverineActivitySource.ExecuteEnvelopeActivityName;
}