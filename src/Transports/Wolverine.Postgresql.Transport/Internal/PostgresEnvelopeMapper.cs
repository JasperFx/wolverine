using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports.Postgresql.Internal;

internal sealed class PostgresEnvelopeMapper : EnvelopeMapper<PostgresMessage, PostgresMessage>
{
    public PostgresEnvelopeMapper(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint,
        runtime)
    {
        MapProperty(x => x.ContentType!,
            (e, m) => e.ContentType = m.ContentType,
            (e, m) => m.ContentType = e.ContentType);
        MapProperty(x => x.Data!,
            (e, m) => e.Data = m.Data,
            (e, m) => m.Data = e.Data);
        MapProperty(x => x.Id, (e, m) => e.Id = m.Id, (e, m) => m.Id = e.Id);
        MapProperty(x => x.CorrelationId!,
            (e, m) => e.CorrelationId = m.CorrelationId,
            (e, m) => m.CorrelationId = e.CorrelationId);
        MapProperty(x => x.MessageType!,
            (e, m) => e.MessageType = m.MessageType,
            (e, m) => m.MessageType = e.MessageType);
        MapProperty(x => x.Attempts,
            (e, m) => e.Attempts = m.Attempts,
            (e, m) => m.Attempts = e.Attempts);
        MapProperty(x => x.ScheduledTime!,
            (e, m) => e.ScheduledTime = m.ScheduledTime,
            (e, m) => m.ScheduledTime = e.ScheduledTime ?? DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    protected override void writeOutgoingHeader(PostgresMessage outgoing, string key, string value)
    {
        outgoing.Headers ??= new Dictionary<string, string>();
        outgoing.Headers[key] = value;
    }

    protected override bool tryReadIncomingHeader(
        PostgresMessage incoming,
        string key,
        out string? value)
    {
        value = null;
        return incoming.Headers?.TryGetValue(key, out value) ?? false;
    }
}
