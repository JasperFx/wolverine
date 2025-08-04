using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Http.Marten;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being loaded as a Marten
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

    public DocumentAttribute(string routeArgumentName) : base(routeArgumentName)
    {
        
    }

    [Obsolete("Prefer the more generic ArgumentName")]
    public string? RouteArgumentName => ArgumentName;
}