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
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();
        
        foreach (var group in groups)
        {
            var message = group.Select(x => x.Message).OfType<T>().ToArray();

            yield return new Envelope(message, group)
            {
                TenantId = group.Key
            };
        }
    }

    public Type BatchMessageType => typeof(T[]);
}
