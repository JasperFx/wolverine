using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten;

/// <summary>
/// Use Marten's FetchLatest() API to retrieve the parameter value
/// </summary>
/// <remarks>
///     GH-3907: the workflow itself is <see cref="ReadModelAttribute" /> in Wolverine core now, and works
///     the same against any event store integration. This is the Marten spelling of it, kept because it is
///     what existing code says. Prefer <c>[ReadModel]</c> in new code.
/// </remarks>
public class ReadAggregateAttribute : ReadModelAttribute
{
    public ReadAggregateAttribute()
    {
    }

    public ReadAggregateAttribute(string argumentName) : base(argumentName)
    {
    }
}
