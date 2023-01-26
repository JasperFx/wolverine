using Lamar;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Http;

public class WolverineRequiredException : Exception
{
    public WolverineRequiredException(Exception? innerException) : base("Wolverine is either not added to this application through IHostBuilder.UseWolverine() or is invalid", innerException)
    {
    }
}

public static class WolverineHttpEndpointRouteBuilderExtensions
{
    public static void MapWolverineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        IWolverineRuntime runtime;

        // TODO -- unit test this behavior
        try
        {
            runtime = endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        }
        catch (Exception e)
        {
            throw new WolverineRequiredException(e);
        }

        
        
        
        // TODO -- let folks customize this somehow? Custom policies? Middleware?
        var container = (IContainer)endpoints.ServiceProvider;
        
        // Making sure this exists
        container.GetInstance<WolverineHttpOptions>();
        var graph = new EndpointGraph(runtime.Options, container);
        
        graph.DiscoverEndpoints().GetAwaiter().GetResult();
        
        endpoints.DataSources.Add(graph);
    }
}