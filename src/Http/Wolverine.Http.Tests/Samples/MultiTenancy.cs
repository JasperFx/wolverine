using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using JasperFx;
using Wolverine.Http.Runtime.MultiTenancy;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Samples;

public static class MultiTenancy
{
    public static async Task<int> bootstrap(params string[] args)
    {
        #region sample_configuring_tenant_id_detection

        var builder = WebApplication.CreateBuilder();

        var connectionString = builder.Configuration.GetConnectionString("postgres");

        builder.Services
            .AddMarten(connectionString)
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Policies.AutoApplyTransactions();
        });

        var app = builder.Build();

        // Configure the WolverineHttpOptions
        app.MapWolverineEndpoints(opts =>
        {
            // The tenancy detection is fall through, so the first strategy
            // that finds anything wins!

            // Use the value of a named request header
            opts.TenantId.IsRequestHeaderValue("tenant");

            // Detect the tenant id from an expected claim in the
            // current request's ClaimsPrincipal
            opts.TenantId.IsClaimTypeNamed("tenant");

            // Use a query string value for the key 'tenant'
            opts.TenantId.IsQueryStringValue("tenant");

            // Use a named route argument for the tenant id
            opts.TenantId.IsRouteArgumentNamed("tenant");

            // Use the *first* sub domain name of the request Url
            // Note that this is very naive
            opts.TenantId.IsSubDomainName();
            
            // If the tenant id cannot be detected otherwise, fallback
            // to a designated tenant id
            opts.TenantId.DefaultIs("default_tenant");

        });

        return await app.RunJasperFxCommands(args);

        #endregion
    }

    public static void require_tenant(WebApplication app)
    {
        #region sample_assert_tenant_id_exists

        app.MapWolverineEndpoints(opts =>
        {
            // Configure your tenant id detection...

            // Require tenant id some how, some way...
            opts.TenantId.AssertExists();
        });

        #endregion
    }

    public static void use_custom_detection(WebApplication app)
    {
        #region sample_registering_custom_tenant_detection

        app.MapWolverineEndpoints(opts =>
        {
            // If your strategy does not need any IoC service
            // dependencies, just add it directly
            opts.TenantId.DetectWith(new MyCustomTenantDetection());


            // In this case, your detection type will be built by
            // the underlying IoC container for your application
            // No other registration is necessary here for the strategy
            // itself
            opts.TenantId.DetectWith<MyCustomTenantDetection>();
        });

        #endregion
    }
}

public class MyCustomTenantDetection : ITenantDetection
{
    public async ValueTask<string?> DetectTenant(HttpContext context)
    {
        throw new NotImplementedException();
    }
}