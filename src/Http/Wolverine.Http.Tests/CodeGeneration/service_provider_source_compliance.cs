using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.CodeGeneration;

/// <summary>
/// GH-4171. Wolverine.HTTP quietly ignored <see cref="ServiceProviderSource"/> for any endpoint or
/// middleware that asked for an <see cref="IServiceProvider"/>: it always got
/// <c>httpContext.RequestServices</c>, and -- because that answer arrived before the service
/// machinery was ever consulted -- it never registered as a service location either, so
/// <c>ServiceLocationPolicy</c> could not see it.
///
/// HTTP chains also never composed the GH-3001 scope priming, so a service-located
/// <see cref="IMessageContext"/> or Marten <see cref="IDocumentSession"/> was a fresh instance
/// rather than the outbox-enrolled one the endpoint already owned.
/// </summary>
public class service_provider_source_compliance
{
    private static async Task<IAlbaHost> buildHost(ServiceProviderSource source,
        ServiceLocationPolicy policy = ServiceLocationPolicy.AlwaysAllowed)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(service_provider_source_compliance).Assembly);
            opts.ServiceLocationPolicy = policy;

            // An 'opaque' scoped lambda: the only way to build it is to ask the container, so any
            // chain that needs it drops onto the service-location path and creates the child scope
            // that the priming is supposed to seed.
            opts.Services.AddScoped<IScopeProbe>(sp => new ScopeProbe(
                sp.GetRequiredService<IMessageContext>(),
                sp.GetRequiredService<IDocumentSession>()));
        });

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "sps_compliance";
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            // Same shape as service_location_assertions.buildHost: a chain that refuses to compile
            // throws inside the endpoint, and whether that exception propagates out of TestServer or
            // is turned into a 500 depends on the host environment -- it differs between a local run
            // and CI. The developer exception page makes it a 500 with the message in the body either way.
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints(opts => opts.ServiceProviderSource = source);
        });
    }

    [Fact]
    public async Task isolated_and_scoped_uses_wolverines_own_child_scope()
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped);

        var result = await host.Scenario(x => x.Get.Url("/service-provider-source"));

        (await result.ReadAsTextAsync()).ShouldBe("isolated");
    }

    [Fact]
    public async Task from_http_context_request_services_uses_the_request_container()
    {
        await using var host = await buildHost(ServiceProviderSource.FromHttpContextRequestServices);

        var result = await host.Scenario(x => x.Get.Url("/service-provider-source"));

        (await result.ReadAsTextAsync()).ShouldBe("request");
    }

    // Asking for IServiceProvider IS service location, and now says so. Previously the derived
    // httpContext.RequestServices variable satisfied the request before ServiceCollectionServerVariableSource
    // was consulted, so the endpoint slipped past NotAllowed entirely -- while the equivalent message
    // handler threw.
    [Fact]
    public async Task asking_for_IServiceProvider_counts_as_service_location()
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped,
            ServiceLocationPolicy.NotAllowed);

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/service-provider-source");
            x.StatusCodeShouldBe(500);
        });

        // ...and specifically because of the IServiceProvider, not just any 500.
        (await result.ReadAsTextAsync()).ShouldContain("IServiceProvider");
    }

    // GH-3001's priming: the child scope is seeded with the endpoint's own MessageContext and its
    // outbox-enrolled Marten session before anything is resolved out of it.
    [Fact]
    public async Task the_child_scope_is_primed_with_the_endpoints_own_instances()
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped);

        var result = await host.Scenario(x => x.Get.Url("/service-provider-source/priming"));

        (await result.ReadAsTextAsync()).ShouldBe("context:same session:same");
    }

    // ...and it is primed even when nothing in the endpoint names an IServiceProvider. This is the
    // shape that silently went unprimed: the scope exists only because IScopeProbe is an opaque scoped
    // registration, and it is not created until after every frame has resolved its variables. See
    // GH-4171 and the ScopePostProcessorSources it moved the priming onto.
    [Fact]
    public async Task priming_does_not_depend_on_the_endpoint_asking_for_a_service_provider()
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped);

        var result = await host.Scenario(x => x.Get.Url("/service-provider-source/implicit-priming"));

        (await result.ReadAsTextAsync()).ShouldBe("context:same session:same");
    }
}

public interface IScopeProbe
{
    IMessageContext Context { get; }
    IDocumentSession Session { get; }
}

public class ScopeProbe(IMessageContext context, IDocumentSession session) : IScopeProbe
{
    public IMessageContext Context { get; } = context;
    public IDocumentSession Session { get; } = session;
}

public static class ServiceProviderSourceEndpoint
{
    [WolverineGet("/service-provider-source")]
    public static string Get(IServiceProvider services, HttpContext httpContext)
    {
        return ReferenceEquals(services, httpContext.RequestServices) ? "request" : "isolated";
    }

    [WolverineGet("/service-provider-source/priming")]
    public static string Priming(IServiceProvider services, IMessageContext context, IDocumentSession session)
    {
        return Compare(services.GetRequiredService<IScopeProbe>(), context, session);
    }

    // No IServiceProvider anywhere. IScopeProbe is an opaque scoped registration, so the scope is
    // created for it alone -- and until GH-4171 that scope was never primed.
    [WolverineGet("/service-provider-source/implicit-priming")]
    public static string ImplicitPriming(IScopeProbe probe, IMessageContext context, IDocumentSession session)
    {
        return Compare(probe, context, session);
    }

    private static string Compare(IScopeProbe probe, IMessageContext context, IDocumentSession session)
    {
        var contextMatch = ReferenceEquals(probe.Context, context) ? "same" : "different";
        var sessionMatch = ReferenceEquals(probe.Session, session) ? "same" : "different";

        return $"context:{contextMatch} session:{sessionMatch}";
    }
}
