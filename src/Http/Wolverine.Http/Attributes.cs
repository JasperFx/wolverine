using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Http;

/// <summary>
///     Base class for attributes that configure how an HTTP endpoint is handled by applying
///     middleware or error handling rules
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class Attributes : Attribute, IModifyChain<HttpChain>
{
    /// <summary>
    ///     Called by Wolverine during bootstrapping before message handlers are generated and compiled
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="rules"></param>
    public abstract void Modify(HttpChain chain, GenerationRules rules);
}

/// <summary>
/// Base class that marks a method as a Wolverine.Http route handler
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public abstract class WolverineHttpMethodAttribute : Attribute
{
    public string HttpMethod { get; }
    public string Template { get; }

    protected WolverineHttpMethodAttribute(string httpMethod, string template)
    {
        HttpMethod = httpMethod;
        Template = template;
    }

    /// <summary>
    /// Override the routing order of this method as necessary to disambiguate routes
    /// </summary>
    public int Order { get; set; }
    
    /// <summary>
    /// Name for the route in ASP.Net Core
    /// </summary>
    public string Name { get; set; }
}

/// <summary>
/// Marks a method on a Wolverine endpoint as being a GET route
/// </summary>
public class WolverineGetAttribute : WolverineHttpMethodAttribute
{
    public WolverineGetAttribute(string template) : base("GET", template)
    {
    }
}

/// <summary>
/// Marks a method on a Wolverine endpoint as being a POST route
/// </summary>
public class WolverinePostAttribute : WolverineHttpMethodAttribute
{
    public WolverinePostAttribute(string template) : base("POST", template)
    {
    }
}

/// <summary>
/// Marks a method on a Wolverine endpoint as being a PUT route
/// </summary>
public class WolverinePutAttribute : WolverineHttpMethodAttribute
{
    public WolverinePutAttribute(string template) : base("PUT", template)
    {
    }
}

/// <summary>
/// Marks a method on a Wolverine endpoint as being a PUT route
/// </summary>
public class WolverineHeadAttribute : WolverineHttpMethodAttribute
{
    public WolverineHeadAttribute(string template) : base("HEAD", template)
    {
    }
}

/// <summary>
/// Marks a method on a Wolverine endpoint as being a DELETE route
/// </summary>
public class WolverineDeleteAttribute : WolverineHttpMethodAttribute
{
    public WolverineDeleteAttribute(string template) : base("DELETE", template)
    {
    }
}


/// <summary>
/// Marks a method on a Wolverine endpoint as being a PATCH route
/// </summary>
public class WolverinePatchAttribute : WolverineHttpMethodAttribute
{
    public WolverinePatchAttribute(string template) : base("PATCH", template)
    {
    }
}



/// <summary>
/// Marks a method on a Wolverine endpoint as being a OPTIONS route
/// </summary>
public class WolverineOptionsAttribute : WolverineHttpMethodAttribute
{
    public WolverineOptionsAttribute(string template) : base("PATCH", template)
    {
    }
}

