using Marten;
using Marten.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.Marten;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Utilize Wolverine's HTTP tenant id detection with non-Wolverine endpoints. Replaces
    /// Marten's session builder with a version that uses the specified tenant id detection
    /// rules to build a lightweight Marten session
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IServiceCollection AddMartenTenancyDetection(this IServiceCollection services, Action<ITenantDetectionPolicies> configure)
    {
        var options = new WolverineHttpOptions();
        configure(options.TenantId);

        services.AddSingleton(options);
        services.AddSingleton<ISessionFactory, WolverineTenantDetectedSessionFactory>();
        services.AddHttpContextAccessor();

        return services;
    }
}

public class WolverineTenantDetectedSessionFactory : SessionFactoryBase
{
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly WolverineHttpOptions _options;

    public WolverineTenantDetectedSessionFactory(IDocumentStore store, IHttpContextAccessor contextAccessor, WolverineHttpOptions options) : base(store)
    {
        _contextAccessor = contextAccessor;
        _options = options;
    }

    public override SessionOptions BuildOptions()
    {
        var options = new SessionOptions
        {
            TenantId = _options.TryDetectTenantIdSynchronously(_contextAccessor.HttpContext),
            Tracking = DocumentTracking.None
        };

        return options;
    }
    
    
}