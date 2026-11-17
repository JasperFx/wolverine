using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Marten;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Subscriptions;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
/// GH-5143: Marten's scoped subscription runner (SubscribeToEventsWithServices) disposed the page's service
/// scope before the after-commit flush, and set no tenant at all. It now shares the singleton runner's tenant
/// rule, which on a single-database store is still "no tenant" (GH-4485).
/// </summary>
public class scoped_subscription_runner_tenant_and_scope_5143 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "scoped_runner5143";
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddScoped<ScopedDependency5143>();
                opts.Services.AddScoped<CapturingSubscription5143>();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task the_scope_outlives_the_flush()
    {
        var runner = new ScopedWolverineSubscriptionRunner<CapturingSubscription5143>(theHost.Services, theHost.GetRuntime());
        var store = theHost.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession("red");

        var listener = await runner.ProcessEventsAsync(null!, null!, session, CancellationToken.None);

        var dependency = CapturingSubscription5143.LastDependency.ShouldNotBeNull();
        dependency.Disposed.ShouldBeFalse();

        await listener.AfterCommitAsync(session, null!, CancellationToken.None);
        dependency.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task a_single_database_store_leaves_the_page_context_untenanted()
    {
        var runner = new ScopedWolverineSubscriptionRunner<CapturingSubscription5143>(theHost.Services, theHost.GetRuntime());
        var store = theHost.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession("red");

        var listener = await runner.ProcessEventsAsync(null!, null!, session, CancellationToken.None);
        await listener.AfterCommitAsync(session, null!, CancellationToken.None);

        // GH-4485: on one database the identifier is the store name, not a tenant
        CapturingSubscription5143.LastTenantId.ShouldBeNull();
    }
}

public class ScopedDependency5143 : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

public class CapturingSubscription5143 : IWolverineSubscription
{
    public static string? LastTenantId;
    public static ScopedDependency5143? LastDependency;

    private readonly ScopedDependency5143 _dependency;

    public CapturingSubscription5143(ScopedDependency5143 dependency)
    {
        _dependency = dependency;
    }

    public string SubscriptionName => "capturing5143";
    public uint SubscriptionVersion { get; set; } = 1;

    public void Filter(IEventFilterable filterable)
    {
    }

    public AsyncOptions Options { get; } = new();

    public Task ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        LastTenantId = bus.TenantId;
        LastDependency = _dependency;
        return Task.CompletedTask;
    }
}
