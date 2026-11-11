using IntegrationTests;
using JasperFx.Events;
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
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "start_stream_key";

                        // These ops start streams by string key. Since JasperFx 3.0 the shared
                        // EventOperationsBase validates the key against the store's StreamIdentity before
                        // anything is read, so a Guid-identity store refuses a string key outright.
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
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
    public string Id { get; set; } = null!;
}

public record PcTripStarted;
