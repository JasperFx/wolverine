using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

/// <summary>
/// Base for the parity hosts. Owns the Wolverine + AspVersioning wiring every host shares —
/// conventional discovery off with this assembly included, <c>AddWolverineHttp()</c>,
/// <c>MapWolverineEndpoints(o =&gt; o.UseAspVersioning())</c>, and host teardown. Subclasses supply only
/// their Asp.Versioning/service configuration and register their native <c>/native/*</c> twins.
/// </summary>
public abstract class ParityFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    protected async Task<IAlbaHost> BuildHost(
        Action<IServiceCollection> configureServices,
        Action<WebApplication> configureApp
    )
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());

        configureServices(builder.Services);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });
        builder.Services.AddWolverineHttp();

        return Host = await AlbaHost.For(
            builder,
            app =>
            {
                configureApp(app);
                app.MapWolverineEndpoints(opts => opts.UseAspVersioning());
            }
        );
    }

    public abstract Task InitializeAsync();

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        await Host.DisposeAsync();
    }
}
