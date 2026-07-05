namespace Wolverine.Runtime.Batching;

/// <summary>
/// An <see cref="IMessageBatcher"/> that de-duplicates the batched messages by a user-supplied key so
/// the handler only sees one message per distinct key (last message wins). Like
/// <c>DefaultMessageBatcher&lt;T&gt;</c> it first groups by tenant id. Crucially it only changes
/// <em>what the handler sees</em>: every member envelope still rides on the batch, so the transactional
/// inbox/outbox settlement and dead-lettering behavior are identical to a non-coalescing batch.
/// </summary>
internal class CoalescingMessageBatcher<T, TKey> : IMessageBatcher
{
    private readonly Func<T, TKey> _keySelector;

    public CoalescingMessageBatcher(Func<T, TKey> keySelector)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
    }

    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        // Group by tenant id exactly like DefaultMessageBatcher<T>. Empty string is the sentinel for a
        // null TenantId because Dictionary<string, ...> does not allow null keys.
        var groups = new Dictionary<string, List<Envelope>>();
        foreach (var envelope in envelopes)
        {
            var tenant = envelope.TenantId ?? string.Empty;
            if (!groups.TryGetValue(tenant, out var list))
            {
                list = new List<Envelope>();
                groups[tenant] = list;
            }

            list.Add(envelope);
        }

        foreach (var group in groups)
        {
            // Coalesce by the user's key (last wins) for the array the handler SEES... Key on object so
            // TKey stays unconstrained (a nullable key type is allowed); null keys are handled below.
            var indexByKey = new Dictionary<object, int>();
            var coalesced = new List<T>(group.Value.Count);
            foreach (var envelope in group.Value)
            {
                if (envelope.Message is not T typed)
                {
                    continue;
                }

                var key = _keySelector(typed);

                // A null key can't index a Dictionary; never coalesce null-keyed items - keep each.
                if (key is not null && indexByKey.TryGetValue(key, out var index))
                {
                    coalesced[index] = typed;
                }
                else
                {
                    if (key is not null)
                    {
                        indexByKey[key] = coalesced.Count;
                    }

                    coalesced.Add(typed);
                }
            }

            // ...but EVERY member envelope stays on the batch, so settlement (inbox/outbox tracking and
            // dead-lettering) is identical to today. Only what the handler sees was reduced.
            yield return new Envelope(coalesced.ToArray(), group.Value)
            {
                TenantId = group.Key.Length == 0 ? null : group.Key
            };
        }
    }

    public Type BatchMessageType => typeof(T[]);
}
