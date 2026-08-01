using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http.Marten;

public static class WolverineHttpOptionsExtensions
{
    /// <summary>
    /// Adds an <see cref="IResourceWriterPolicy"/> that streams <see cref="ICompiledQuery"/>
    /// </summary>
    /// <param name="options">Options to apply policy on</param>
    public static void UseMartenCompiledQueryResultPolicy(this WolverineHttpOptions options,
        string responseType = "application/json", int successStatusCode = 200)
    {
        options.AddResourceWriterPolicy(new CompiledQueryWriterPolicy(responseType, successStatusCode));
    }

    /// <summary>
    /// Registers the <see cref="MartenConcurrencyExceptionHandler"/> (plus AddProblemDetails()) so the
    /// ASP.NET Core exception handler middleware responds with a ProblemDetails body and the supplied
    /// status code (409 Conflict by default) when a Marten concurrency exception escapes an HTTP
    /// endpoint, and a <see cref="ConcurrencyExceptionPolicy"/> that registers the conflict response
    /// in the OpenAPI metadata of the Wolverine endpoints where it is reachable. The application must
    /// also call <c>app.UseExceptionHandler()</c> for the handler to run
    /// </summary>
    /// <param name="services">The application's service collection</param>
    /// <param name="statusCode">The HTTP status code of the ProblemDetails response. Default is 409 (Conflict)</param>
    public static IServiceCollection UseProblemDetailsForConcurrencyExceptions(this IServiceCollection services,
        int statusCode = 409)
    {
        services.AddProblemDetails();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, MartenConcurrencyExceptionHandler>(
            sp => new MartenConcurrencyExceptionHandler(statusCode,
                sp.GetRequiredService<ILogger<MartenConcurrencyExceptionHandler>>())));

        // IChainPolicy so it can be registered at service-registration time; Wolverine.Http applies
        // core chain policies to the HTTP chains, and the policy no-ops for messaging chains
        services.ConfigureWolverine(opts => opts.Policies.Add(new ConcurrencyExceptionPolicy(statusCode)));

        return services;
    }
}
