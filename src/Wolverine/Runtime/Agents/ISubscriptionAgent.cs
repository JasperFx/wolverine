namespace Wolverine.Runtime.Agents;

// TODO -- this should probably go to JasperFx.Events later
public interface ISubscriptionAgent
{
    Task StartRebuildAsync(CancellationToken cancellation);
    Task RewindSubscriptionAsync(long? sequenceFloor, DateTimeOffset? timestamp, CancellationToken cancellation);
}