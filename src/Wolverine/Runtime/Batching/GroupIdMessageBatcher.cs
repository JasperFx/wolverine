using Wolverine.Runtime.Partitioning;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Implemented by an <see cref="IMessageBatcher"/> that needs the application's message
/// partitioning rules in order to group. Supplied by the batching processor builder, which is the
/// first point at which the <see cref="WolverineRuntime"/> is available.
/// </summary>
internal interface IRequirePartitioningRules
{
    MessagePartitioningRules Rules { set; }
}

/// <summary>
/// Batches messages by their <see cref="Envelope.GroupId"/> (and tenant), and stamps that group id
/// onto each batch envelope it produces. Opt in with <see cref="BatchingOptions.GroupByGroupId"/>.
/// </summary>
/// <remarks>
/// <para>The default batcher groups only by tenant, so a single batch envelope can span many group
/// ids and therefore carries none of its own. That matters as soon as batching is combined with
/// partitioned sequential processing: <see cref="PartitionedMessagingExtensions.SlotForProcessing"/>
/// falls back to <b>a randomly chosen slot</b> when it cannot determine a group id, so an unstamped
/// batch lands on a different slot on every trigger and is serialized against nothing. Grouping the
/// batch by group id makes the batch itself a member of exactly one group, which is what lets a
/// batched handler participate in the same sequential ordering as the unbatched handlers for the
/// same entity.</para>
///
/// <para>Tenancy is still honoured — the grouping key is (tenant, group id), never group id alone.
/// Wolverine settles a batch's members against the batch envelope, so mixing tenants into one
/// envelope would lose the tenant each member arrived under.</para>
///
/// <para>Envelopes whose group id cannot be determined are batched together and the resulting
/// envelope is left ungrouped, which is the same treatment they get today. This batcher does not
/// invent a group id for them.</para>
/// </remarks>
internal class GroupIdMessageBatcher<T> : IMessageBatcher, IRequirePartitioningRules
{
    private MessagePartitioningRules? _rules;

    public MessagePartitioningRules Rules
    {
        set => _rules = value;
    }

    public Type BatchMessageType => typeof(T[]);

    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        // Dictionary<string,...> disallows null keys, so both slots use "" as the null sentinel and
        // are mapped back on the way out.
        var groups = new Dictionary<(string Tenant, string Group), List<Envelope>>();

        foreach (var envelope in envelopes)
        {
            // Prefer whatever is already on the envelope; DetermineGroupId does that first anyway,
            // and also writes the resolved value back so later stages do not re-resolve it. On a
            // listener using PartitionProcessingByGroupId this has already happened upstream.
            var group = _rules?.DetermineGroupId(envelope) ?? envelope.GroupId;

            var key = (envelope.TenantId ?? string.Empty, group ?? string.Empty);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<Envelope>();
                groups[key] = list;
            }

            list.Add(envelope);
        }

        foreach (var pair in groups)
        {
            var members = pair.Value;
            var messages = new List<T>(members.Count);
            foreach (var envelope in members)
            {
                if (envelope.Message is T typed)
                {
                    messages.Add(typed);
                }
            }

            yield return new Envelope(messages.ToArray(), members)
            {
                TenantId = pair.Key.Tenant.Length == 0 ? null : pair.Key.Tenant,
                GroupId = pair.Key.Group.Length == 0 ? null : pair.Key.Group
            };
        }
    }
}
