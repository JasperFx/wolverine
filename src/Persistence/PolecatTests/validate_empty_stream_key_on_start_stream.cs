using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;

namespace PolecatTests;

public class validate_empty_stream_key_on_start_stream : IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore _store;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "start_stream_val";
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void assert_empty_stream_key()
    {
        // Empty stream key should throw at construction time
        Should.Throw<InvalidOperationException>(() =>
            PolecatOps.StartStream<PcTrip>("", new PcTripStarted()));
    }

    [Fact]
    public void assert_null_stream_key()
    {
        // Null stream key should throw at construction time
        Should.Throw<InvalidOperationException>(() =>
            PolecatOps.StartStream<PcTrip>(null!, new PcTripStarted()));
    }

    [Fact]
    public async Task happy_path_execution()
    {
        await using var session = _store.LightweightSession();

        var op = PolecatOps.StartStream<PcTrip>("a good key", new PcTripStarted());
        op.Execute(session);
    }
}

public class PcTrip
{
    public Guid Id { get; set; }
}

public record PcTripStarted;
