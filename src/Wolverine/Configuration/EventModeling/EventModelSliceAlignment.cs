using JasperFx.Events.EventModeling;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     Reconcile the slice <em>names</em> of several sources before they are merged, so a declared Event
///     Model and the code that implements it fold into one model instead of two disconnected halves
///     (GH-4385).
/// </summary>
/// <remarks>
///     <para>
///         <b>The problem.</b> <see cref="EventModelSliceDescriptor.Name" /> is the merge key, and the two
///         kinds of source compute it from different things. A declaration carries the board's slice name —
///         <c>ConfirmAppointment</c>, <c>AppointmentsQueue</c> — which is a name for the <em>behaviour</em>.
///         Wolverine's derived sources name a slice for what they can see: the message type
///         (<c>ConfirmAppointmentRequest</c>), the triggering event (<c>HomeCheckAssignmentAccepted</c>), or
///         the route (<c>GET /api/appointmentsqueue/{id}</c>). Both are reasonable; neither is the other. On
///         an eleven-slice sample the result was twenty-two slices and zero hotspots — not because the
///         sources agreed, but because they never met, and the provenance ladder (Declared &lt; Derived
///         &lt; Observed, with a <c>SourceDisagreement</c> where they conflict) never engaged at all.
///     </para>
///     <para>
///         <b>The join.</b> <see cref="EventModelSliceDescriptor.HandlerType" /> is the one role both kinds
///         of source can fill for the same slice and mean the same thing by: the curated format has a
///         <c>handler:</c> role for exactly this, and every Wolverine chain knows its handler or endpoint
///         type. Where two rungs describe the same handler type under different names, this renames the
///         higher rung's slice to the lower rung's name — which is not a new rule so much as the existing
///         one applied at the join: naming is a declaration concern (jasperfx#703's "slice names keep
///         coming from declarations: nothing else claims them", and the GH-4181 reasoning that left
///         <c>TriggerLabel</c> unclaimed). Neither side renames anything in its own file; the merge that
///         follows then does what it was built to do, raising the real disagreements as hotspots instead of
///         silently duplicating every slice.
///     </para>
///     <para>
///         <b>What it deliberately will not do.</b> Only rungs that <em>differ</em> align — two Derived
///         sources (Wolverine core and Wolverine.HTTP) already agree on how they name things, and a handler
///         type they happen to share is not evidence that they describe one slice. A handler type carrying
///         more than one slice inside a single descriptor is ambiguous and is skipped rather than guessed
///         at, which is the ordinary shape of a handler class that handles several messages. And a rename
///         that would collide with a slice already in the descriptor is dropped, because collapsing two
///         distinct slices into one is worse than leaving them unjoined.
///     </para>
/// </remarks>
public static class EventModelSliceAlignment
{
    /// <summary>
    ///     Rename slices across <paramref name="descriptors" /> so sources on different provenance rungs
    ///     that describe the same handler type agree on the slice name. Returns the descriptors unchanged
    ///     when there is nothing to join.
    /// </summary>
    /// <param name="descriptors">The discovered descriptors, in registration order. Order breaks ties within a rung.</param>
    public static IReadOnlyList<EventModelDescriptor> AlignSliceNames(IReadOnlyList<EventModelDescriptor> descriptors)
    {
        if (descriptors.Count < 2) return descriptors;

        // Per descriptor: the one slice each handler type owns. A handler type carrying two or more
        // slices in the same descriptor cannot identify one of them, so it identifies none.
        var byHandler = descriptors.Select(unambiguousSlicesByHandler).ToArray();

        var winners = new Dictionary<string, (EventModelProvenance Rung, string Name)>(StringComparer.Ordinal);
        var contested = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < byHandler.Length; i++)
        {
            foreach (var (handler, slice) in byHandler[i])
            {
                var rung = slice.Provenance ?? EventModelProvenance.Declared;

                if (!winners.TryGetValue(handler, out var current))
                {
                    winners[handler] = (rung, slice.Name);
                    continue;
                }

                // Only a *different* rung is evidence of a declaration meeting the code it describes.
                if (rung != current.Rung) contested.Add(handler);

                // The lowest rung names the slice; a tie keeps the first descriptor's name.
                if (rung < current.Rung) winners[handler] = (rung, slice.Name);
            }
        }

        if (contested.Count == 0) return descriptors;

        var results = new List<EventModelDescriptor>(descriptors.Count);
        var changed = false;
        for (var i = 0; i < descriptors.Count; i++)
        {
            var renamed = rename(descriptors[i], byHandler[i], winners, contested);
            changed |= !ReferenceEquals(renamed, descriptors[i]);
            results.Add(renamed);
        }

        // Sources that already agreed on a name come back untouched, not merely equal: nothing
        // downstream should have to tell an alignment that did nothing from one that did.
        return changed ? results : descriptors;
    }

    private static Dictionary<string, EventModelSliceDescriptor> unambiguousSlicesByHandler(EventModelDescriptor descriptor)
    {
        var owned = new Dictionary<string, EventModelSliceDescriptor>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var slice in descriptor.Slices)
        {
            if (slice.HandlerType?.FullName is not { } handler) continue;
            if (!owned.TryAdd(handler, slice)) ambiguous.Add(handler);
        }

        foreach (var handler in ambiguous) owned.Remove(handler);

        return owned;
    }

    private static EventModelDescriptor rename(
        EventModelDescriptor descriptor,
        Dictionary<string, EventModelSliceDescriptor> byHandler,
        Dictionary<string, (EventModelProvenance Rung, string Name)> winners,
        HashSet<string> contested)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(descriptor.Slices.Select(x => x.Name), StringComparer.Ordinal);

        foreach (var (handler, slice) in byHandler)
        {
            if (!contested.Contains(handler)) continue;
            if (!winners.TryGetValue(handler, out var winner)) continue;
            if (string.Equals(slice.Name, winner.Name, StringComparison.Ordinal)) continue;

            // A name already spoken for in this descriptor belongs to some other slice; taking it would
            // fold two distinct slices into one, which is a worse answer than leaving this one unjoined.
            if (!taken.Add(winner.Name)) continue;

            renames[handler] = winner.Name;
        }

        if (renames.Count == 0) return descriptor;

        // Keyed by handler type rather than by the old name: only the one slice that handler owns is
        // renamed, whatever else in the descriptor happens to share its name.
        var slices = descriptor.Slices
            .Select(slice => slice.HandlerType?.FullName is { } handler && renames.TryGetValue(handler, out var name)
                ? slice with { Name = name }
                : slice)
            .ToList();

        return descriptor with { Slices = slices };
    }
}
