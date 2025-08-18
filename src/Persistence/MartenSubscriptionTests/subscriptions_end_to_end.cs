using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Subscriptions;
using Wolverine.Tracking;
using Xunit;

namespace MartenSubscriptionTests;

public class subscriptions_end_to_end
{
    private async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("subscriptions");
        await conn.CloseAsync();
    }

    [Fact]
    public async Task use_unfiltered_batch_subscription()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "subscriptions";
                }).IntegrateWithWolverine()
                .UseLightweightSessions()
                .SubscribeToEvents(new TestBatchSubscription());
            }).StartAsync();

        var runtime = host.GetRuntime();
        var routing = runtime.RoutingFor(typeof(IEvent<AEvent>));

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new CEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new DEvent(), new AEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new BEvent(), new BEvent(), new BEvent());

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        // 4 event types, 4 totals. Might be broken up by page
        tracked.Executed.MessagesOf<EventTotalsUpdated>().Count().ShouldBeGreaterThanOrEqualTo(4);

        using var query = store.QuerySession();
        (await query.LoadAsync<EventTotals>("A")).Count.ShouldBe(6);
        (await query.LoadAsync<EventTotals>("B")).Count.ShouldBe(7);
        (await query.LoadAsync<EventTotals>("C")).Count.ShouldBe(5);
        (await query.LoadAsync<EventTotals>("D")).Count.ShouldBe(6);
    }

    [Fact]
    public async Task use_filtered_batch_subscription()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                var subscription = new TestBatchSubscription();
                subscription.IncludeType<AEvent>();
                subscription.IncludeType<BEvent>();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "subscriptions";
                }).IntegrateWithWolverine()
                .UseLightweightSessions()
                .SubscribeToEvents(subscription);
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new CEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new DEvent(), new AEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new BEvent(), new BEvent(), new BEvent());

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        // 4 event types, 2 allow list. Might be broken up by page
        tracked.Executed.MessagesOf<EventTotalsUpdated>().Count().ShouldBeGreaterThanOrEqualTo(2);

        using var query = store.QuerySession();
        (await query.LoadAsync<EventTotals>("A")).Count.ShouldBe(6);
        (await query.LoadAsync<EventTotals>("B")).Count.ShouldBe(7);
        (await query.LoadAsync<EventTotals>("C")).ShouldBeNull();
        (await query.LoadAsync<EventTotals>("D")).ShouldBeNull();
    }

    [Fact]
    public async Task use_inline_subscription()
    {
        TotalsHandler.Handled.Clear();
        await dropSchema();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "subscriptions";
                }).IntegrateWithWolverine()
                .UseLightweightSessions()
                .ProcessEventsWithWolverineHandlersInStrictOrder("Inline");
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
        session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
        session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(30.Seconds());

        TotalsHandler.Handled.ShouldBe(['a', 'b', 'd', 'd', 'a', 'a', 'a', 'a', 'b', 'c', 'c', 'b']);
    }

    [Fact]
    public async Task use_inline_subscription_filtered()
    {
        TotalsHandler.Handled.Clear();
        await dropSchema();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .ProcessEventsWithWolverineHandlersInStrictOrder("Inline", s =>
                    {
                        s.IncludeType<AEvent>();
                        s.IncludeType<BEvent>();
                    });
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
        session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
        session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(30.Seconds());

        TotalsHandler.Handled.ShouldBe(['a', 'b', 'a', 'a', 'a', 'a', 'b', 'b']);
    }

    [Fact]
    public async Task use_unfiltered_publishing_subscription()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish");
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new CEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new DEvent(), new AEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new BEvent(), new BEvent(), new BEvent(), new Event1(Guid.NewGuid()));

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        tracked.Executed.MessagesOf<IEvent<AEvent>>().Count().ShouldBe(6);
        tracked.Executed.MessagesOf<BEvent>().Count().ShouldBe(7);
        tracked.Executed.MessagesOf<IEvent<DEvent>>().Count().ShouldBe(6);
    }

        [Fact]
    public async Task use_filtered_publishing_subscription()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish", x =>
                    {
                        x.PublishEvent<AEvent>();
                        x.PublishEvent<DEvent>();
                    });
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new CEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new DEvent(), new AEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new BEvent(), new BEvent(), new BEvent(), new Event1(Guid.NewGuid()));

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        tracked.Executed.MessagesOf<IEvent<AEvent>>().Count().ShouldBe(6);

        // Filtered out
        tracked.Executed.MessagesOf<BEvent>().Count().ShouldBe(0);
        tracked.Executed.MessagesOf<IEvent<DEvent>>().Count().ShouldBe(6);
    }

    [Fact]
    public async Task carry_the_tenant_id_through_on_the_subscription()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                        m.Policies.AllDocumentsAreMultiTenanted();
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish", x =>
                    {
                        x.PublishEvent<AEvent>();
                        x.PublishEvent<DEvent>((e, bus) => bus.PublishAsync(new TransformedMessage('D')));
                    });
            }).StartAsync();
        
        var store = host.DocumentStore();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession("one");
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent());


            await session.SaveChangesAsync();

            await using var session2 = store.LightweightSession("two");
            session2.Events.StartStream(Guid.NewGuid(), new BEvent(), new DEvent(), new DEvent());
            await session2.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);
        
        tracked.MessageSucceeded.RecordsInOrder().ShouldNotBeEmpty();

        tracked.MessageSucceeded.Envelopes().Where(x => x.Message is IEvent<AEvent>).Each(e =>
        {
            e.TenantId.ShouldBe("one");
        });

        tracked.MessageSucceeded.Envelopes().Where(x => x.Message is BEvent).Each(e =>
        {
            e.TenantId.ShouldBe("two");
        });
    }

            [Fact]
    public async Task use_transformed_publishing_subscription()
    {
        await dropSchema();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish", x =>
                    {
                        x.PublishEvent<AEvent>();
                        x.PublishEvent<DEvent>((e, bus) => bus.PublishAsync(new TransformedMessage('D')));
                    });
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new CEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new DEvent(), new AEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new DEvent(), new BEvent(), new BEvent(), new BEvent(), new Event1(Guid.NewGuid()));

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        tracked.Executed.MessagesOf<IEvent<AEvent>>().Count().ShouldBe(6);

        // Filtered out
        tracked.Executed.MessagesOf<BEvent>().Count().ShouldBe(0);
        tracked.Executed.MessagesOf<IEvent<DEvent>>().Count().ShouldBe(0);
        tracked.Executed.MessagesOf<TransformedMessage>().Count().ShouldBe(6);
    }

    [Fact]
    public async Task using_singleton_scoped_subscription_from_service()
    {
        TaggingService.InstanceCount = 0;
        ServiceUsingSubscription.Read.Clear();
        await dropSchema();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Services.AddSingleton<TaggingService>();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .SubscribeToEventsWithServices<ServiceUsingSubscription>(ServiceLifetime.Singleton);
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
        session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
        session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(20.Seconds());


        // Second round
        session.Events.StartStream(Guid.NewGuid(), new DEvent(), new DEvent(), new DEvent(), new DEvent());
        await session.SaveChangesAsync();
        // await daemon.WaitForNonStaleData(20.Seconds());

        ServiceUsingSubscription.Read.Count().ShouldBe(1);
        ServiceUsingSubscription.Read[1].OfType<AEvent>().Count().ShouldBe(5);
        ServiceUsingSubscription.Read[1].OfType<BEvent>().Count().ShouldBe(3);
    }

    [Fact]
    public async Task using_scoped_subscription_from_service()
    {
        TaggingService.InstanceCount = 0;
        ServiceUsingSubscription.Read.Clear();
        await dropSchema();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Purposely using scoped here
                opts.Services.AddScoped<TaggingService>();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "subscriptions";
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .SubscribeToEventsWithServices<ServiceUsingSubscription>(ServiceLifetime.Scoped);
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        await using var session = store.LightweightSession();

        // Rigging this up so the daemon will have to use multiple batches
        for (int i = 0; i < 50; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new BEvent(), new DEvent(), new DEvent());
            session.Events.StartStream(Guid.NewGuid(), new AEvent(), new AEvent(), new AEvent(), new AEvent());
            session.Events.StartStream(Guid.NewGuid(), new BEvent(), new CEvent(), new CEvent(), new BEvent());

            await session.SaveChangesAsync();
        }

        await daemon.WaitForNonStaleData(60.Seconds());

        ServiceUsingSubscription.Read.Count().ShouldBeGreaterThan(1);
    }
}

public record Event1(Guid Id);

public class TestBatchSubscription : BatchSubscription
{
    public TestBatchSubscription() : base("BatchTest")
    {
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var totals = await operations
            .Query<EventTotals>()
            .ToListAsync(token: cancellationToken);

        var cache = new LightweightCache<string, EventTotals>(letter => new EventTotals { Id = letter });

        foreach (var total in totals)
        {
            cache[total.Id] = total;
        }

        foreach (var e in page.Events)
        {
            if (e.Data is AEvent)
            {
                cache["A"].Count++;
            }

            if (e.Data is BEvent)
            {
                cache["B"].Count++;
            }

            if (e.Data is CEvent)
            {
                cache["C"].Count++;
            }

            if (e.Data is DEvent)
            {
                cache["D"].Count++;
            }
        }

        foreach (var total in cache)
        {
            operations.Store(total);
            await bus.PublishAsync(new EventTotalsUpdated(total));
        }
    }
}

public class EventTotals
{
    public string Id { get; set; }
    public int Count { get; set; }
}

public record EventTotalsUpdated(EventTotals totals);

public static class EventTotalsUpdatedHandler
{
    public static void Handle(EventTotalsUpdated updated)
    {
        // don't need to do anything
    }
}

public record TransformedMessage(char Letter);

public static class TotalsHandler
{
    public static List<char> Handled { get; } = new();

    public static void Handle(TransformedMessage message)
    {

    }

    public static void Handle(IEvent<AEvent> e)
    {
        Handled.Add('a');
    }

    public static void Handle(BEvent e)
    {
        Handled.Add('b');
    }

    public static void Handle(CEvent e)
    {
        Handled.Add('c');
    }

    public static void Handle(IEvent<DEvent> e)
    {
        Handled.Add('d');
    }
}

public class ServiceUsingSubscription : BatchSubscription
{
    private readonly TaggingService _service;

    public static LightweightCache<int, List<object>> Read { get; } = new(i => new());

    public ServiceUsingSubscription(TaggingService service) : base("Tagged")
    {
        _service = service;
    }

    public override Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        Read[_service.Instance].AddRange(page.Events.Select(x => x.Data));
        return Task.CompletedTask;
    }
}

public class TaggingService
{
    public static int InstanceCount = 0;

    public TaggingService()
    {
        Instance = ++InstanceCount;
    }

    public int Instance { get; set; }
}

public record AEvent;

public record BEvent;

public record CEvent;

public record DEvent;