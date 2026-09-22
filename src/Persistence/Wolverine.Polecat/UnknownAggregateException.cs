using JasperFx.Core.Reflection;

namespace Wolverine.Polecat;

/// <summary>
/// Thrown from the aggregate handler workflow when a <i>required</i> aggregate parameter's event stream
/// does not exist or has no events.
/// </summary>
/// <remarks>
/// GH-4513: the message names the remedy, because the fix is a decision the handler author has to make --
/// either let this throw, or make the parameter optional and decide in the handler what a missing stream
/// means. Note that an HTTP endpoint never gets here: a missing required aggregate stops the request with
/// a 404 instead.
/// </remarks>
public class UnknownAggregateException : Exception
{
    public UnknownAggregateException(Type aggregateType, object id) : base(
        $"Could not find an aggregate of type {aggregateType.FullNameInCode()} with id {id}. The parameter is required; to handle a missing stream in the handler instead of failing, make the parameter nullable or use [WriteAggregate(Required = false)] (or the equivalent on [ReadAggregate]/[Aggregate]).")
    {
    }
}
