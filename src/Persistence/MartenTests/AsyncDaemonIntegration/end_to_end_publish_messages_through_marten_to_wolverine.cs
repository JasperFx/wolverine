using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Events.Daemon.Resiliency;
using Marten.Events.Projections;
using Marten.Metadata;
using Marten.Schema;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit.Sdk;

namespace MartenTests.AsyncDaemonIntegration;

public class end_to_end_publish_messages_through_marten_to_wolverine
{
    [Fact]
    public async Task can_publish_messages_through_outbox()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "wolverine_side_effects";

                        m.Projections.Add<Projection3>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);
                
                opts.Policies.UseDurableLocalQueues();
            }).StartAsync();

        var streamId = Guid.NewGuid();

        Func<IMessageContext, Task> publish = async _ =>
        {
            using var session = host.DocumentStore().LightweightSession();
            session.Events.StartStream<SideEffects1>(streamId, new AEvent(), new AEvent(), new BEvent());
            await session.SaveChangesAsync();
        };
        
        var tracked = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<GotB>(host)
            .ExecuteAndWaitAsync(publish);
        
        tracked.Executed.SingleMessage<GotB>()
            .StreamId.ShouldBe(streamId);
    }
    
    [Fact]
    public async Task can_publish_messages_through_outbox_running_inline()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "wolverine_side_effects";
                        
                        m.Projections.Add<Projection3>(ProjectionLifecycle.Inline);
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);
                
                opts.Policies.UseDurableLocalQueues();
            }).StartAsync();

        var streamId = Guid.NewGuid();

        Func<IMessageContext, Task> publish = async _ =>
        {
            using var session = host.DocumentStore().LightweightSession();
            session.Events.StartStream<SideEffects1>(streamId, new AEvent(), new AEvent(), new BEvent());
            await session.SaveChangesAsync();
        };
        
        var tracked = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<GotB>(host)
            .ExecuteAndWaitAsync(publish);
        
        tracked.Executed.SingleMessage<GotB>()
            .StreamId.ShouldBe(streamId);
    }
    
    [Fact]
    public async Task can_publish_messages_through_outbox_with_tenancy()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "wolverine_side_effects";

                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                        m.Schema.For<SideEffects1>().MultiTenanted();

                        m.Projections.Add<Projection3>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);
                
                opts.Policies.UseDurableLocalQueues();
            }).StartAsync();

        var streamId = Guid.NewGuid();

        Func<IMessageContext, Task> publish = async _ =>
        {
            using var session = host.DocumentStore().LightweightSession("one");
            session.Events.StartStream<SideEffects1>(streamId, new AEvent(), new AEvent(), new BEvent());
            await session.SaveChangesAsync();
        };

        var tracked = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<GotB>(host)
            .ExecuteAndWaitAsync(publish);
        
        tracked.Executed.SingleMessage<GotB>()
            .StreamId.ShouldBe(streamId);
        
        tracked.Executed.SingleEnvelope<GotB>()
            .TenantId.ShouldBe("one");
    }
    
    
    [Fact]
    public async Task can_publish_messages_through_outbox_running_inline_from_within_initial_data()
    {
        await dropSchema();
        
        GotBHandler.Received.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "wolverine_side_effects";

                        m.Projections.Add<Projection3>(ProjectionLifecycle.Inline);
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        
                        
                    })
                    .IntegrateWithWolverine();
                ;
                    //.AddAsyncDaemon(DaemonMode.Solo)
                    //.InitializeWith<SideEffectInitialData>();

                    opts.Services.AddHostedService<SideEffectInitialData>();
                
                opts.Policies.UseDurableLocalQueues();
            }).StartAsync();

        var count = 0;
        while (count < 10)
        {
            if (GotBHandler.Received.Count >= 3) break;
            await Task.Delay(250.Milliseconds());
        }
        
        GotBHandler.Received.Count.ShouldBe(3);
    }


    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("wolverine_side_effects");
        await conn.CloseAsync();
    }
}

public class Projection3: SingleStreamProjection<SideEffects1>
{
    public void Apply(SideEffects1 aggregate, AEvent _)
    {
        aggregate.A++;
    }

    public void Apply(SideEffects1 aggregate, BEvent _)
    {

    }
    
    public void Apply(SideEffects1 aggregate, CEvent _)
    {

    }

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<SideEffects1> slice)
    {
        if (slice.Aggregate != null && slice.Events().OfType<IEvent<BEvent>>().Any())
        {
            slice.PublishMessage(new GotB(slice.Aggregate.Id));
        }

        return new ValueTask();
    }
}

public record GotB(Guid StreamId);

public static class GotBHandler
{
    public static List<GotB> Received { get; } = new();
    
    public static void Handle(GotB message)
    {
        Received.Add(message);
        Debug.WriteLine("Got B for stream " + message.StreamId);
    }
}

public class SideEffects1: IRevisioned
{
    public Guid Id { get; set; }
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int D { get; set; }
    public int Version { get; set; }
}

// Wrap it in your own IHostedService
public class SideEffectInitialData : IInitialData, IHostedService
{
    private readonly IDocumentStore _store;

    public SideEffectInitialData(IDocumentStore store)
    {
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Populate(_store, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task Populate(IDocumentStore store, CancellationToken cancellation)
    {
        using var session = store.LightweightSession();
        session.Events.StartStream<SideEffects1>(new AEvent(), new AEvent(), new BEvent());
        session.Events.StartStream<SideEffects1>(new AEvent(), new BEvent(), new BEvent());
        session.Events.StartStream<SideEffects1>(new BEvent(), new BEvent(), new BEvent());
        await session.SaveChangesAsync(cancellation);
    }
}
