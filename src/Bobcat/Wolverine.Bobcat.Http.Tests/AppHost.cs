using Alba;
using IntegrationTests;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.AspNetCore.Builder;
using Wolverine.Http;
using Wolverine.Marten;

namespace Wolverine.Bobcat.Http.Tests;

public sealed class AppHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_http_specs";
                m.DisableNpgsqlLogging = true;
                m.Projections.Snapshot<Appointment>(SnapshotLifecycle.Inline);
            })
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(AppHost).Assembly);
        });

        builder.Services.AddWolverineHttp();

        Host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints()).StartAsync();
        await Host.CleanAllMartenDataAsync();
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

[CollectionDefinition(nameof(AppCollection))]
public class AppCollection : ICollectionFixture<AppHost>;
