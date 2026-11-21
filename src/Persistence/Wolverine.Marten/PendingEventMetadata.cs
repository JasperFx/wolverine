using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Internal;

namespace Wolverine.Marten;

/// <summary>
/// Assigns some of the metadata the commit writes to pending events before the commit, for an aggregate projected from
/// them.
/// </summary>
public static class PendingEventMetadata
{
    /// <summary>
    /// On every pending event in <paramref name="session" />, sets Timestamp (whole microseconds) if it has none, Version
    /// when the stream's expected version is known, and the causation id, correlation id and user name if the event has
    /// none and the value is enabled. Leaves values already on an event alone, Version aside. Does not set the tenant
    /// id, headers or Sequence, or Version without an expected version. Call it right before projecting from them.
    /// </summary>
    /// <param name="session">A session Marten itself created, not a wrapper around one.</param>
    public static void Stamp(IDocumentSession session)
    {
        if (session is not IMartenSession martenSession || session is not IMetadataContext metadata)
        {
            throw new ArgumentException(
                $"PendingEventMetadata.Stamp needs a session Marten itself created, but got a {session.GetType().FullName}. A wrapper around a Marten session can't be stamped.",
                nameof(session));
        }

        var streams = session.PendingChanges.Streams();
        if (streams.Count == 0) return;

        var now = martenSession.Options.Events.TimeProvider.GetUtcNow();
        var timestamp = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));

        foreach (var stream in streams)
        {
            // Numbered as StreamAction.PrepareEvents does; without an expected version only the commit knows
            var version = stream.ExpectedVersionOnServer ?? (stream.ActionType == StreamActionType.Start ? 0 : null);

            foreach (var @event in stream.Events)
            {
                if (@event.Timestamp == default)
                {
                    @event.Timestamp = timestamp;
                }

                if (version.HasValue)
                {
                    version++;
                    @event.Version = version.Value;
                }

                if (metadata.CausationIdEnabled)
                {
                    @event.CausationId ??= metadata.CausationId;
                }

                if (metadata.CorrelationIdEnabled)
                {
                    @event.CorrelationId ??= metadata.CorrelationId;
                }

                if (metadata.UserNameEnabled)
                {
                    @event.UserName ??= metadata.CurrentUserName;
                }
            }
        }
    }
}
