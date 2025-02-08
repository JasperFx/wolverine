using IntegrationTests;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

public class handler_actions_with_returned_StartStream : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore _store;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(Servers.PostgresConnectionString)
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
    public async Task start_stream_by_guid1()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartStreamMessage(id));

        using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<AEvent>();
        events[1].Data.ShouldBeOfType<BEvent>();
    }
}

public class start_stream_by_string_from_return_value : PostgresqlContext, IAsyncLifetime
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
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.DatabaseSchemaName = "string_identity";
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
    public async Task start_stream_by_string()
    {
        var id = Guid.NewGuid().ToString();

        await _host.InvokeMessageAndWaitAsync(new StartStreamMessage2(id));

        using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<CEvent>();
        events[1].Data.ShouldBeOfType<BEvent>();
    }
}

public record StartStreamMessage(Guid Id);
public record StartStreamMessage2(string Id);

public static class StartStreamMessageHandler
{
    public static IStartStream Handle(StartStreamMessage message)
    {
        return MartenOps.StartStream<NamedDocument>(message.Id, new AEvent(), new BEvent());
    }

    public static IStartStream Handle(StartStreamMessage2 message)
    {
        return MartenOps.StartStream<NamedDocument>(message.Id, new CEvent(), new BEvent());
    }
}