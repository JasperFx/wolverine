using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;

namespace LoadTesting.Trips;

public static class StartTripHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException().MoveToErrorQueue();
    }

    public static void Before()
    {
        // Chaos monkey
        if (Random.Shared.Next() < .05)
        {
            throw new TripServiceTooBusyException("Just feeling tired at " + DateTime.Now);
        }
    }
    
    public static IStartStream Handle(StartTrip command, ILogger logger)
    {
        logger.LogInformation("Starting a new trip {Id}", command.TripId);
        return MartenOps.StartStream<Trip>(command.TripId, new TripStarted(command.StartDay, command.State));
    }
}