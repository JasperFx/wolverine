using System;
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

        services.AddSingleton<Action<HttpContext, IDocumentSession>>((_, _) => { });

        return services;
    }
    
    /// <summary>
    /// Utilize Wolverine's HTTP tenant id detection with non-Wolverine endpoints. Replaces
    /// Marten's session builder with a version that uses the specified tenant id detection
    /// rules to build a lightweight Marten session
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <param name="metadata"></param>
    /// <returns></returns>
    public static IServiceCollection AddMartenTenancyDetection(this IServiceCollection services, Action<ITenantDetectionPolicies> configure, Action<HttpContext, IDocumentSession> metadata)
    {
        var options = new WolverineHttpOptions();
        configure(options.TenantId);

        services.AddSingleton(options);
        services.AddSingleton<ISessionFactory, WolverineTenantDetectedSessionFactory>();
        services.AddHttpContextAccessor();

        services.AddSingleton(metadata);

        return services;
    }
}

public class WolverineTenantDetectedSessionFactory : SessionFactoryBase
{
    private readonly IDocumentStore _store;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly WolverineHttpOptions _options;
    private readonly Action<HttpContext, IDocumentSession> _metadataSource;

    public WolverineTenantDetectedSessionFactory(IDocumentStore store, IHttpContextAccessor contextAccessor, WolverineHttpOptions options, Action<HttpContext, IDocumentSession> metadataSource) : base(store)
    {
        _store = store;
        _contextAccessor = contextAccessor;
        _options = options;
        _metadataSource = metadataSource;
    }

    public override IQuerySession QuerySession()
    {
        if (_contextAccessor.HttpContext == null) return _store.QuerySession();
        
        var tenantId = _options.TryDetectTenantIdSynchronously(_contextAccessor.HttpContext);

        return _store.QuerySession(tenantId);
    }

    public override SessionOptions BuildOptions()
    {
        var tenantId = _contextAccessor.HttpContext == null ? null : _options.TryDetectTenantIdSynchronously(_contextAccessor.HttpContext);
        var options = new SessionOptions
        {
            TenantId = tenantId,
            Tracking = DocumentTracking.None
        };

        return options;
    }

    public override void ApplyMetadata(IDocumentSession documentSession)
    {
        if (_contextAccessor.HttpContext != null)
        {
            _metadataSource(_contextAccessor.HttpContext, documentSession);
        }
    }
}