using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Http;

/// <summary>
///     Base class for attributes that configure how an HTTP endpoint is handled by applying
///     middleware or error handling rules
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class ModifyHttpChainAttribute : Attribute, IModifyChain<HttpChain>
{
    /// <summary>
    ///     Called by Wolverine during bootstrapping before message handlers are generated and compiled
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="rules"></param>
    public abstract void Modify(HttpChain chain, GenerationRules rules);
}

/// <summary>
///     Base class for attributes that configure how an HTTP endpoint on a parameter is handled by applying
///     middleware or error handling rules
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public abstract class HttpChainParameterAttribute : WolverineParameterAttribute
{
    /// <summary>
    ///     Called by Wolverine during bootstrapping to modify the code generation
    /// for an HTTP endpoint with the decorated parameter
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="parameter"></param>
    /// <param name="container"></param>
    public abstract Variable Modify(HttpChain chain, ParameterInfo parameter,
        IServiceContainer container);
}

/// <summary>
///     Base class that marks a method as a Wolverine.Http route handler
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public abstract class WolverineHttpMethodAttribute : Attribute
{
    protected WolverineHttpMethodAttribute(string httpMethod, string template)
    {
        HttpMethod = httpMethod;
        Template = template;
    }

    public string HttpMethod { get; }
    public string Template { get; }
    
    public string? RouteName { get; set; }

    /// <summary>
    ///     Override the routing order of this method as necessary to disambiguate routes
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Name for the route in ASP.Net Core
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Overrides the OperationId property on HttpChain
    /// Can be used to seed OpenAPI documentation with
    /// Swashbuckle
    /// </summary>
    public string? OperationId { get; set; }
}

/// <summary>
/// Explicitly makes this HTTP endpoint opt out of any tenancy requirements
/// </summary>
public class NotTenantedAttribute : ModifyHttpChainAttribute
{
    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        chain.TenancyMode = TenancyMode.None;
    }
}

/// <summary>
/// Tell Wolverine that this endpoint can work with or without
/// a detected tenant
/// </summary>
public class MaybeTenantedAttribute : ModifyHttpChainAttribute
{
    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        chain.TenancyMode = TenancyMode.Maybe;
    }
}

/// <summary>
/// Enforce that this endpoint must have a tenant id
/// </summary>
public class RequiresTenantAttribute : ModifyHttpChainAttribute
{
    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        chain.TenancyMode = TenancyMode.Required;
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a GET route
/// </summary>
public class WolverineGetAttribute : WolverineHttpMethodAttribute
{
    public WolverineGetAttribute([StringSyntax("Route")]string template) : base("GET", template)
    {
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a POST route
/// </summary>
public class WolverinePostAttribute : WolverineHttpMethodAttribute
{
    public WolverinePostAttribute([StringSyntax("Route")]string template) : base("POST", template)
    {
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a PUT route
/// </summary>
public class WolverinePutAttribute : WolverineHttpMethodAttribute
{
    public WolverinePutAttribute([StringSyntax("Route")]string template) : base("PUT", template)
    {
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a PUT route
/// </summary>
public class WolverineHeadAttribute : WolverineHttpMethodAttribute
{
    public WolverineHeadAttribute([StringSyntax("Route")]string template) : base("HEAD", template)
    {
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a DELETE route
/// </summary>
public class WolverineDeleteAttribute : WolverineHttpMethodAttribute
{
    public WolverineDeleteAttribute([StringSyntax("Route")]string template) : base("DELETE", template)
    {
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a PATCH route
/// </summary>
public class WolverinePatchAttribute : WolverineHttpMethodAttribute
{
    public WolverinePatchAttribute([StringSyntax("Route")]string template) : base("PATCH", template)
    {
    }
}

/// <summary>
///     Marks a method on a Wolverine endpoint as being a OPTIONS route
/// </summary>
public class WolverineOptionsAttribute : WolverineHttpMethodAttribute
{
    public WolverineOptionsAttribute([StringSyntax("Route")]string template) : base("OPTIONS", template)
    {
    }
}
