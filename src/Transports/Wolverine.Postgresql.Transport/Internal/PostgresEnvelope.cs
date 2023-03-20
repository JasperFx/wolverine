namespace Wolverine.Transports.Postgresql.Internal;

internal sealed class PostgresEnvelope : Envelope
{
    public PostgresEnvelope(PostgresMessage message)
    {
        PostgresMessage = message;
    }

    public PostgresMessage PostgresMessage { get; }
}