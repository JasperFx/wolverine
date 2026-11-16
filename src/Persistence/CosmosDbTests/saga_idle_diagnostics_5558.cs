using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

// GH-5558: idle saga diagnostics over CosmosDB saga documents. IdleDiagnosticsSaga is this host's only saga type,
// which is what lets the untyped saga documents be counted
[Collection("cosmosdb")]
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    private readonly AppFixture _fixture;

    public saga_idle_diagnostics_5558(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Services.AddSingleton(_fixture.Client);
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
    }
}

// GH-5558: with two saga types in the container, their documents cannot be told apart, so idle queries are refused
// rather than answered with a count that mixes them
[Collection("cosmosdb")]
public class saga_idle_diagnostics_with_several_saga_types_5558 : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _host = null!;

    public saga_idle_diagnostics_with_several_saga_types_5558(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<IdleDiagnosticsSaga>()
                    .IncludeType<CosmosDiagSaga>();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task idle_queries_are_refused()
    {
        var idle = (ISagaIdleDiagnostics)_host.GetRuntime().SagaStorage;
        var name = typeof(IdleDiagnosticsSaga).FullName!;

        idle.SupportsIdleQueries(name).ShouldBeFalse();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            idle.CountIdleSagasAsync(name, DateTimeOffset.UtcNow, null, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("only saga type");
    }
}
