using Wolverine.Configuration;

namespace Wolverine.Marten;

/// <summary>
///     Tells Wolverine handlers that this value contains a
///     list of events to be appended to the current stream
/// </summary>
public class Events : List<object>, IWolverineReturnType
{
    public Events()
    {
    }

    public Events(IEnumerable<object> collection) : base(collection)
    {
    }

    public static Events operator +(Events events, object @event)
    {
        events.Add(@event);
        return events;
    }
}