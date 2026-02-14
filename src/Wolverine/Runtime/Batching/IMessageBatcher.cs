using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Batching;

#region sample_IMessageBatcher

/// <summary>
/// Plugin strategy for creating custom grouping of messages
/// </summary>
public interface IMessageBatcher
{
    /// <summary>
    /// Main method that batches items
    /// </summary>
    /// <param name="envelopes"></param>
    /// <returns></returns>
    IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes);
    
    /// <summary>
    /// The actual message type being built that is assumed to contain
    /// all the batched items
    /// </summary>
    Type BatchMessageType { get; }
}

#endregion

internal class DefaultMessageBatcher<T> : IMessageBatcher
{
    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        // Group by tenant id
        var groups = new Dictionary<string?, List<Envelope>>();
        foreach (var envelope in envelopes)
        {
            if (!groups.TryGetValue(envelope.TenantId, out var list))
            {
                list = new List<Envelope>();
                groups[envelope.TenantId] = list;
            }

            list.Add(envelope);
        }

        foreach (var group in groups)
        {
            var messages = new List<T>(group.Value.Count);
            foreach (var envelope in group.Value)
            {
                if (envelope.Message is T typed)
                {
                    messages.Add(typed);
                }
            }

            yield return new Envelope(messages.ToArray(), group.Value)
            {
                TenantId = group.Key
            };
        }
    }

    public Type BatchMessageType => typeof(T[]);
}
