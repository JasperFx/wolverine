namespace Wolverine.Runtime.Batching;

public interface IMessageBatcher
{
    IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes);
    Type BatchMessageType { get; }
}

internal class DefaultMessageBatcher<T> : IMessageBatcher
{
    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        // Group by tenant id
        var groups = envelopes.GroupBy(x => x.TenantId).ToArray();
        
        foreach (var group in groups)
        {
            var message = group.Select(x => x.Message).OfType<T>().ToArray();

            foreach (var envelope in group)
            {
                envelope.InBatch = true;
            }
            
            var grouped = new Envelope(message)
            {
                Batch = group.ToArray(),
                TenantId = group.Key
            };

            yield return grouped;
        }
    }

    public Type BatchMessageType => typeof(T[]);
}