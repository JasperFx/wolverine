using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Fisher;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Fisher.Subscriptions;
using Wolverine.Tracking;

namespace FisherTests.Subscriptions;

/// <summary>
/// GH-5143: the scoped subscription runner (SubscribeToEventsWithServices) set no tenant on the page's
/// message context, although the singleton runner used the session's tenant. Both now share one rule.
/// Also pins that the page's service scope lives until the after-commit flush.
/// </summary>
public class scoped_subscription_runner_tenant_and_scope_5143 : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("scoped_runner5143");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Policies.AllDocumentsAreMultiTenanted();
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddScoped<ScopedDependency5143>();
                opts.Services.AddScoped<CapturingSubscription5143>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task the_page_context_carries_the_session_tenant_and_the_scope_outlives_the_flush()
    {
        var runner = new ScopedWolverineSubscriptionRunner<CapturingSubscription5143>(theHost.Services, theHost.GetRuntime());
        var store = theHost.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession("red");

        var listener = await runner.ProcessEventsAsync(null!, null!, session, CancellationToken.None);

        CapturingSubscription5143.LastTenantId.ShouldBe("red");
        var dependency = CapturingSubscription5143.LastDependency.ShouldNotBeNull();
        dependency.Disposed.ShouldBeFalse();

        await listener.AfterCommitAsync(CancellationToken.None);
        dependency.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task a_default_tenant_session_leaves_the_page_context_untenanted()
    {
        var runner = new ScopedWolverineSubscriptionRunner<CapturingSubscription5143>(theHost.Services, theHost.GetRuntime());
        var store = theHost.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession();

        var listener = await runner.ProcessEventsAsync(null!, null!, session, CancellationToken.None);
        await listener.AfterCommitAsync(CancellationToken.None);

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
    public uint Version { get; set; } = 1;

    public void Filter(IEventFilterable filterable)
    {
    }

    public AsyncOptions Options { get; } = new();

    public Task ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentSession operations,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        LastTenantId = bus.TenantId;
        LastDependency = _dependency;
        return Task.CompletedTask;
    }
}
