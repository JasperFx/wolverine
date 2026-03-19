using IntegrationTests;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using MartenTests.AggregateHandlerWorkflow;
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
public class handler_actions_with_returned_StartStream_with_tenant_switching : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore _store;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(o =>
                    {
                        o.Connection(Servers.PostgresConnectionString);
                        o.Policies.AllDocumentsAreMultiTenanted();
                        o.Events.TenancyStyle = TenancyStyle.Conjoined;
                        o.DatabaseSchemaName = "martenops_events_guid";
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));
        await _store.Advanced.Clean.DeleteAllEventDataAsync();
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

        await _host.InvokeMessageAndWaitAsync(new StartStreamMessage(id, "green"));

        using var session = _store.LightweightSession("green");
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

public class start_stream_by_string_from_return_value_with_tenant_switching : PostgresqlContext, IAsyncLifetime
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
                        m.Policies.AllDocumentsAreMultiTenanted();
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.DatabaseSchemaName = "martenops_string_identity";
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
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

        await _host.InvokeMessageAndWaitAsync(new StartStreamMessage2(id, "green"));

        using var session = _store.LightweightSession("green");
        var events = await session.Events.FetchStreamAsync(id);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<CEvent>();
        events[1].Data.ShouldBeOfType<BEvent>();
    }
}

public record StartStreamMessage(Guid Id, string? TenantId = null);
public record StartStreamMessage2(string Id, string? TenantId = null);

public static class StartStreamMessageHandler
{
    public static IStartStream Handle(StartStreamMessage message)
    {
        if (message.TenantId is not null)
            return MartenOps.StartStream<NamedDocument>(message.Id, message.TenantId, new AEvent(), new BEvent());
        
        return MartenOps.StartStream<NamedDocument>(message.Id, new AEvent(), new BEvent());
    }

    public static IStartStream Handle(StartStreamMessage2 message)
    {
        if (message.TenantId is not null)
            return MartenOps.StartStream<NamedDocument>(message.Id, message.TenantId, new CEvent(), new BEvent());

        return MartenOps.StartStream<NamedDocument>(message.Id, new CEvent(), new BEvent());
    }
}