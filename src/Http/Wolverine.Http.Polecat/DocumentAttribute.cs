using Wolverine.Attributes;
using Wolverine.Persistence;

namespace Wolverine.Http.Polecat;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being loaded as a Polecat
///     document identified by a route argument. If the route argument
///     is not specified, this would look for either "typeNameId" or "id".
///
/// This is 100% equivalent to the more generic [Entity] attribute now
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class DocumentAttribute : EntityAttribute
{
    public DocumentAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public DocumentAttribute(string argumentName) : base(argumentName)
    {
    }
}
