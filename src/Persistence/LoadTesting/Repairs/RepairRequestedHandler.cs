using LoadTesting.Trips;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

public static class RepairRequestedHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException().MoveToErrorQueue();
    }

    public static void Before(RepairRequested requested)
    {
        // Chaos monkey
        if (Random.Shared.NextDouble() < .05)
        {
            throw new RepairShopTooBusyException(requested.State + " is just too busy");
        }
    }
    
    // Just splitting them
    public static object Handle(RepairRequested requested)
    {
        var localQueue = new Uri($"local://{requested.State.ToLowerInvariant()}");
        return new ConductRepairs(requested.TripId).ToDestination(localQueue);
    }
}