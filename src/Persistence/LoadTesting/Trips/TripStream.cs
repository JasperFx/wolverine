using JasperFx.Core;

namespace LoadTesting.Trips;

public class TripStream
{
    public static List<TripStream> RandomStreams(int number)
    {
        var list = new List<TripStream>();
        for (var i = 0; i < number; i++)
        {
            var stream = new TripStream();
            list.Add(stream);
        }

        return list;
    }

    public static readonly string[] States = new string[] {"Texas", "Arkansas", "Missouri", "Kansas", "Oklahoma", "Connecticut", "New Jersey", "New York" };

    public static string RandomState()
    {
        var index = Random.Shared.Next(0, States.Length - 1);
        return States[index];
    }

    public static Direction RandomDirection()
    {
        var index = Random.Shared.Next(0, 3);
        switch (index)
        {
            case 0:
                return Direction.East;
            case 1:
                return Direction.North;
            case 2:
                return Direction.South;
            default:
                return Direction.West;
        }
    }

    public static TimeOnly RandomTime()
    {
        var hour = Random.Shared.Next(0, 24);
        return new TimeOnly(hour, 0, 0);
    }

    public Guid Id = CombGuidIdGeneration.NewGuid();

    public readonly Queue<object> Messages = new();

    public TripStream()
    {
        var random = Random.Shared;
        var startDay = random.Next(1, 100);

        Messages.Enqueue(new StartTrip(Id, startDay, RandomState()));

        var state = RandomState();

        Messages.Enqueue(new Depart(Id, startDay, state));

        var duration = random.Next(1, 20);

        var randomNumber = random.NextDouble();
        for (var i = 0; i < duration; i++)
        {
            var day = startDay + i;

            var travel = new RecordTravel(Id, Traveled.Random(day));
            Messages.Enqueue(travel);

            if (i > 0 && randomNumber > .3)
            {
                var departure = new Depart(Id, day, state);

                Messages.Enqueue(departure);

                state = RandomState();

                var arrival = new Arrive(Id, i, state);
                Messages.Enqueue(arrival);
            }
            
            if (randomNumber < .05)
            {
                Messages.Enqueue(new RecordBreakdown(Id, true));
            }
            else if (randomNumber < .08)
            {
                Messages.Enqueue(new RecordBreakdown(Id, false));
            }
        }

        if (randomNumber > .5)
        {
            Messages.Enqueue(new EndTrip(Id, startDay + duration, state));
        }
        else if (randomNumber > .9)
        {
            Messages.Enqueue(new AbortTrip(Id));
        }
        
    }

    public bool IsFinishedPublishing()
    {
        return !Messages.Any();
    }

    public bool TryCheckoutCommand(out object command)
    {
        return (Messages.TryDequeue(out command));
    }


    public string TenantId { get; set; }

}