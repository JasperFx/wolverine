using System.Diagnostics;
using System.Text.Json;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
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
using IRevisioned = Marten.Metadata.IRevisioned;

namespace MartenTests;

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

public class Projection3 : SingleStreamProjection<SideEffects1, Guid>
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
        if (slice.Snapshot != null && slice.Events().OfType<IEvent<BEvent>>().Any())
        {
            slice.PublishMessage(new GotB(slice.Snapshot.Id));
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
    }
}

public class SideEffects1 : IRevisioned
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

public class
    side_effect_messaging_with_inline_projections_and_mix_of_tenanted_and_not_tenanted_elements : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "mixed_tenancy";
                    m.Schema.For<Customer>().SingleTenanted();
                    m.Schema.For<Order2>().MultiTenanted();
                    m.Events.TenancyStyle = TenancyStyle.Conjoined;

                    m.Projections.Add<CustomerProjection>(ProjectionLifecycle.Inline);
                    m.Events.EnableSideEffectsOnInlineProjections = true;
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }
    
    [Fact]
    public async Task expect_message_from_non_tenanted_session()
    {
        var store = _host.DocumentStore();
        await using var session = store.LightweightSession();
        var customerId = session.Events.StartStream<Customer>(new CustomerAdded("Acme")).Id;
        await session.SaveChangesAsync();

        Func<IMessageContext, Task> action = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.Append(customerId, new CustomerMoved("Jasper"));
            await session.SaveChangesAsync();
        };

        var tracked = await _host
            .TrackActivity()
            .Timeout(2.Minutes())
            .WaitForMessageToBeReceivedAt<CustomerChanged>(_host)
            .ExecuteAndWaitAsync(action);

        tracked.Executed.SingleMessage<CustomerChanged>()
            .Customer.Location.ShouldBe("Jasper");
    }

    [Fact]
    public async Task expect_message_from_tenanted_session()
    {
        var store = _host.DocumentStore();
        await using var session = store.LightweightSession();
        var customerId = session.Events.StartStream<Customer>(new CustomerAdded("Acme")).Id;
        await session.SaveChangesAsync();

        Func<IMessageContext, Task> action = async _ =>
        {
            await using var session = store.LightweightSession("aaa");
            session.ForTenant(StorageConstants.DefaultTenantId).Events.Append(customerId, new CustomerMoved("Jasper"));
            await session.SaveChangesAsync();
        };

        var tracked = await _host
            .TrackActivity()
            .Timeout(2.Minutes())
            .WaitForMessageToBeReceivedAt<CustomerChanged>(_host)
            .ExecuteAndWaitAsync(action);

        tracked.Executed.SingleMessage<CustomerChanged>()
            .Customer.Location.ShouldBe("Jasper");
    }
}

public static class CustomerChangedHandler
{
    public static void Handle(CustomerChanged changed) => Debug.WriteLine(JsonSerializer.Serialize(changed.Customer));
}

public class Customer
{
    public Guid Id { get; set; }
    public  string Name { get; set; }
    public bool IsActive { get; set; }
    public string Location { get; set; }
}

public record CustomerAdded(string Name);

public record CustomerActivated;

public record CustomerMoved(string Location);

public class CustomerProjection : MultiStreamProjection<Customer, Guid>
{
    public CustomerProjection()
    {
        TenancyGrouping = TenancyGrouping.AcrossTenants;
        Identity<IEvent>(x => x.StreamId);
    }

    public static Customer Create(CustomerAdded added) => new Customer { Name = added.Name };

    public void Apply(Customer customer, CustomerActivated _) => customer.IsActive = true;
    public void Apply(Customer customer, CustomerMoved moved) => customer.Location = moved.Location;

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Customer> slice)
    {
        if (slice.Aggregate != null && slice.Aggregate.Location.IsNotEmpty())
        {
            slice.PublishMessage(new CustomerChanged(slice.Aggregate));
        }

        return new ValueTask();
    }
}

public record CustomerChanged(Customer Customer);



public class OrderItem
{
    public string Name { get; set; }
    public bool Ready { get; set; }
}

public class Order2
{
    // This would be the stream id
    public Guid Id { get; set; }

    // This is important, by Marten convention this would
    // be the
    public int Version { get; set; }

    public Order2(OrderCreated created)
    {
        foreach (var item in created.Items)
        {
            Items[item.Name] = item;
        }
    }

    public void Apply(IEvent<OrderShipped> shipped) => Shipped = shipped.Timestamp;
    public void Apply(ItemReady ready) => Items[ready.Name].Ready = true;

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, OrderItem> Items { get; set; } = new();

    public bool IsReadyToShip()
    {
        return Shipped == null && Items.Values.All(x => x.Ready);
    }
}

public record OrderShipped;
public record OrderCreated(OrderItem[] Items);
public record OrderReady;

public record ItemReady(string Name);

