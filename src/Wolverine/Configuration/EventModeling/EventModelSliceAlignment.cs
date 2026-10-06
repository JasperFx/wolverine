using JasperFx.Descriptors;
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
///     <para>
///         <b>Stub-first joins</b> (GH-4831). A model declared before the code exists has no handler type to
///         join on, so the declared and derived halves slid past each other again. The other roles a stub
///         pins down are joins too, tried in this order once the handler type has had its turn, and each
///         with the same ambiguity and collision guards: the <b>triggering message and its domain</b> (one
///         module's handler of a message several modules handle, GH-4829); the <b>triggering message</b> —
///         the event an Automation reacts to (<c>On&lt;T&gt;()</c>), else the command (<c>Command&lt;T&gt;()</c>,
///         <c>Automation&lt;TCommand&gt;()</c>), which is also the request type Wolverine.HTTP names its slice
///         for; and the <b>read model</b> of a View. A stub <em>is</em> the real type, so these joins are
///         exact, and a declaration by name (no assembly) matches the type of that name. A slice joined on
///         an earlier key is never joined again on a later one.
///     </para>
/// </remarks>
public static class EventModelSliceAlignment
{
    /// <summary>
    ///     Rename slices across <paramref name="descriptors" /> so sources on different provenance rungs
    ///     that describe the same slice — the same handler type, or (GH-4831) the same triggering message,
    ///     domain or read model — agree on the slice name. Returns the descriptors unchanged when there is
    ///     nothing to join.
    /// </summary>
    /// <param name="descriptors">The discovered descriptors, in registration order. Order breaks ties within a rung.</param>
    public static IReadOnlyList<EventModelDescriptor> AlignSliceNames(IReadOnlyList<EventModelDescriptor> descriptors)
    {
        if (descriptors.Count < 2) return descriptors;

        var current = descriptors.ToArray();
        var joined = new HashSet<(int Descriptor, int Slice)>();
        var changed = false;

        foreach (var join in Joins)
        {
            changed |= align(current, join, joined);
        }

        // Sources that already agreed on a name come back untouched, not merely equal: nothing
        // downstream should have to tell an alignment that did nothing from one that did.
        return changed ? current : descriptors;
    }

    /// <summary>What two slices on different rungs can agree on, most specific first.</summary>
    private static readonly Func<EventModelSliceDescriptor, JoinKey?>[] Joins =
    {
        slice => slice.HandlerType?.FullName is { } handler ? new JoinKey(null, handler, null) : null,
        slice => triggerOf(slice) is { } trigger && slice.Domain is { } domain ? new JoinKey(trigger, null, domain) : null,
        slice => triggerOf(slice) is { } trigger ? new JoinKey(trigger, null, null) : null,
        slice => slice.Pattern == SlicePattern.View && slice.ReadModelTypes.Count == 1
            ? new JoinKey(slice.ReadModelTypes[0], "view", null)
            : null,
    };

    // The message that starts a slice: the one event an Automation reacts to, else its command. A View
    // is started by nothing it handles, so it joins on its read model instead.
    private static TypeDescriptor? triggerOf(EventModelSliceDescriptor slice)
    {
        if (slice.Pattern == SlicePattern.View) return null;
        return slice.ConsumedEvents.Count == 1 ? slice.ConsumedEvents[0] : slice.CommandType;
    }

    private sealed record JoinKey(TypeDescriptor? Type, string? Text, string? Domain)
    {
        public bool Matches(JoinKey other)
            => string.Equals(Text, other.Text, StringComparison.Ordinal)
               && string.Equals(Domain, other.Domain, StringComparison.Ordinal)
               && (Type is null
                   ? other.Type is null
                   : other.Type is not null && EventModelSliceDescriptor.SameType(Type, other.Type));
    }

    private sealed record Candidate(int Descriptor, int Slice, string Name, EventModelProvenance Rung, JoinKey Key);

    private static bool align(EventModelDescriptor[] descriptors, Func<EventModelSliceDescriptor, JoinKey?> keyOf,
        HashSet<(int Descriptor, int Slice)> joined)
    {
        var candidates = new List<Candidate>();
        for (var i = 0; i < descriptors.Length; i++)
        {
            var slices = descriptors[i].Slices;
            for (var j = 0; j < slices.Count; j++)
            {
                if (joined.Contains((i, j))) continue;
                if (keyOf(slices[j]) is not { } key) continue;
                candidates.Add(new Candidate(i, j, slices[j].Name, slices[j].Provenance ?? EventModelProvenance.Declared, key));
            }
        }

        // A key carried by two or more slices of the same descriptor cannot identify one of them, so it
        // identifies none -- and neither does one that matches two slices of some other descriptor.
        var usable = candidates.Where(c => !candidates.Any(other => !ReferenceEquals(other, c) &&
                                                                   other.Descriptor == c.Descriptor &&
                                                                   other.Key.Matches(c.Key)))
            .ToList();
        usable = usable.Where(c => usable.Where(other => other.Descriptor != c.Descriptor && other.Key.Matches(c.Key))
                .GroupBy(other => other.Descriptor)
                .All(group => group.Count() == 1))
            .ToList();

        var clusters = new List<List<Candidate>>();
        foreach (var candidate in usable)
        {
            var cluster = clusters.FirstOrDefault(x => x.All(member => member.Key.Matches(candidate.Key)));
            if (cluster is null) clusters.Add(new List<Candidate> { candidate });
            else cluster.Add(candidate);
        }

        var changed = false;
        foreach (var cluster in clusters)
        {
            // Only a *different* rung is evidence of a declaration meeting the code it describes.
            if (cluster.Select(x => x.Rung).Distinct().Count() < 2) continue;

            // The lowest rung names the slice; a tie keeps the first descriptor's name.
            var winner = cluster.OrderBy(x => x.Rung).ThenBy(x => x.Descriptor).First().Name;

            foreach (var member in cluster)
            {
                if (string.Equals(member.Name, winner, StringComparison.Ordinal))
                {
                    joined.Add((member.Descriptor, member.Slice));
                    continue;
                }

                // A name already spoken for in this descriptor belongs to some other slice; taking it would
                // fold two distinct slices into one, which is a worse answer than leaving this one unjoined.
                var descriptor = descriptors[member.Descriptor];
                if (descriptor.Slices.Any(x => string.Equals(x.Name, winner, StringComparison.Ordinal))) continue;

                var slices = descriptor.Slices.ToList();
                slices[member.Slice] = slices[member.Slice] with { Name = winner };
                descriptors[member.Descriptor] = descriptor with { Slices = slices };
                joined.Add((member.Descriptor, member.Slice));
                changed = true;
            }
        }

        return changed;
    }
}
