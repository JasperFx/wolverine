using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

public class validate_empty_stream_key_on_start_stream: PostgresqlContext, IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore _store;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "start_stream";
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void assert_empty_stream_key()
    {
        using var session = _store.LightweightSession();
        
        // No stream key supplied
        var op = MartenOps.StartStream<Trip>(new TripStarted());

        Should.Throw<InvalidOperationException>(() => op.Execute(session));
    }

    [Fact]
    public void happy_path_execution()
    {
        using var session = _store.LightweightSession();
        
        // No stream key supplied
        var op = MartenOps.StartStream<Trip>("a good key", new TripStarted());
        op.Execute(session);
    }
}