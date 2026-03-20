using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Polecat;
using Polecat.Internal;
using Polecat.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Polecat.Subscriptions;
using Wolverine.Tracking;

namespace PolecatTests.Subscriptions;

public class subscriptions_end_to_end
{
    /// <summary>
    ///     Cleans event data and documents from a Polecat schema before the host starts.
    ///     This prevents the auto-started daemon from processing stale events.
    /// </summary>
    private static async Task PreCleanSchemaAsync(string schemaName)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        // Clean events, streams, and progression in FK-safe order (if tables exist)
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF OBJECT_ID('[{schemaName}].pc_events', 'U') IS NOT NULL DELETE FROM [{schemaName}].pc_events;
            IF OBJECT_ID('[{schemaName}].pc_streams', 'U') IS NOT NULL DELETE FROM [{schemaName}].pc_streams;
            IF OBJECT_ID('[{schemaName}].polecat_event_progression', 'U') IS NOT NULL DELETE FROM [{schemaName}].polecat_event_progression;
            """;
        await cmd.ExecuteNonQueryAsync();

        // Clean all document tables in the schema
        cmd.CommandText = $"""
            DECLARE @sql NVARCHAR(MAX) = '';
            SELECT @sql = @sql + 'DELETE FROM [{schemaName}].' + QUOTENAME(TABLE_NAME) + ';'
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{schemaName}' AND TABLE_TYPE = 'BASE TABLE'
              AND TABLE_NAME NOT LIKE 'pc_%' AND TABLE_NAME NOT LIKE 'polecat_%';
            IF LEN(@sql) > 0 EXEC sp_executesql @sql;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task use_unfiltered_batch_subscription()
    {
        const string schema = "pc_subscriptions";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = schema;
                }).IntegrateWithWolverine()
                .UseLightweightSessions()
                .SubscribeToEvents(new PcTestBatchSubscription());

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.CleanAllEventDataAsync();
        await store.Advanced.CleanAllDocumentsAsync();

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcCEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcDEvent(), new PcAEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcBEvent(), new PcBEvent(), new PcBEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(30.Seconds());

        await using var query = store.QuerySession();
        (await query.LoadAsync<PcEventTotals>("A")).Count.ShouldBe(6);
        (await query.LoadAsync<PcEventTotals>("B")).Count.ShouldBe(7);
        (await query.LoadAsync<PcEventTotals>("C")).Count.ShouldBe(5);
        (await query.LoadAsync<PcEventTotals>("D")).Count.ShouldBe(6);
    }

    [Fact]
    public async Task use_filtered_batch_subscription()
    {
        const string schema = "pc_subscriptions_filtered";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                var subscription = new PcTestBatchSubscription();
                subscription.IncludeType<PcAEvent>();
                subscription.IncludeType<PcBEvent>();

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = schema;
                }).IntegrateWithWolverine()
                .UseLightweightSessions()
                .SubscribeToEvents(subscription);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.CleanAllEventDataAsync();
        await store.Advanced.CleanAllDocumentsAsync();

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcCEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcDEvent(), new PcAEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcBEvent(), new PcBEvent(), new PcBEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(30.Seconds());

        await using var query = store.QuerySession();
        (await query.LoadAsync<PcEventTotals>("A")).Count.ShouldBe(6);
        (await query.LoadAsync<PcEventTotals>("B")).Count.ShouldBe(7);
        (await query.LoadAsync<PcEventTotals>("C")).ShouldBeNull();
        (await query.LoadAsync<PcEventTotals>("D")).ShouldBeNull();
    }

    [Fact]
    public async Task use_inline_subscription()
    {
        const string schema = "pc_subscriptions_inline";
        await PreCleanSchemaAsync(schema);
        PcTotalsHandler.Handled.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = schema;
                }).IntegrateWithWolverine()
                .UseLightweightSessions()
                .AddAsyncDaemon(DaemonMode.Solo)
                .ProcessEventsWithWolverineHandlersInStrictOrder("Inline");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        var daemon = host.Services.GetRequiredService<PolecatDaemonHostedService>().Daemon!;

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(30.Seconds());

        PcTotalsHandler.Handled.ShouldBe(['a', 'b', 'd', 'd', 'a', 'a', 'a', 'a', 'b', 'c', 'c', 'b']);
    }

    [Fact]
    public async Task use_inline_subscription_filtered()
    {
        const string schema = "pc_subscriptions_inline_filt";
        await PreCleanSchemaAsync(schema);
        PcTotalsHandler.Handled.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = schema;
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .AddAsyncDaemon(DaemonMode.Solo)
                    .ProcessEventsWithWolverineHandlersInStrictOrder("Inline", s =>
                    {
                        s.IncludeType<PcAEvent>();
                        s.IncludeType<PcBEvent>();
                    });

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        var daemon = host.Services.GetRequiredService<PolecatDaemonHostedService>().Daemon!;

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(30.Seconds());

        PcTotalsHandler.Handled.ShouldBe(['a', 'b', 'a', 'a', 'a', 'a', 'b', 'b']);
    }

    [Fact(Skip = "Known TrackActivity race condition with publishing subscriptions — same failure in MartenSubscriptionTests")]
    public async Task use_unfiltered_publishing_subscription()
    {
        const string schema = "pc_subscriptions_pub";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = schema;
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.CleanAllEventDataAsync();
        await store.Advanced.CleanAllDocumentsAsync();

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcCEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcDEvent(), new PcAEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcBEvent(), new PcBEvent(), new PcBEvent(), new PcEvent1(Guid.NewGuid()));

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        tracked.Executed.MessagesOf<IEvent<PcAEvent>>().Count().ShouldBe(6);
        tracked.Executed.MessagesOf<PcBEvent>().Count().ShouldBe(7);
        tracked.Executed.MessagesOf<IEvent<PcDEvent>>().Count().ShouldBe(6);
    }

    [Fact(Skip = "Known TrackActivity race condition with publishing subscriptions — same failure in MartenSubscriptionTests")]
    public async Task use_filtered_publishing_subscription()
    {
        const string schema = "pc_subscriptions_pub_filt";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = schema;
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish", x =>
                    {
                        x.PublishEvent<PcAEvent>();
                        x.PublishEvent<PcDEvent>();
                    });

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.CleanAllEventDataAsync();
        await store.Advanced.CleanAllDocumentsAsync();

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcCEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcDEvent(), new PcAEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcBEvent(), new PcBEvent(), new PcBEvent(), new PcEvent1(Guid.NewGuid()));

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        tracked.Executed.MessagesOf<IEvent<PcAEvent>>().Count().ShouldBe(6);

        // Filtered out
        tracked.Executed.MessagesOf<PcBEvent>().Count().ShouldBe(0);
        tracked.Executed.MessagesOf<IEvent<PcDEvent>>().Count().ShouldBe(6);
    }

    [Fact]
    public async Task use_transformed_publishing_subscription()
    {
        const string schema = "pc_subscriptions_transform";
        await PreCleanSchemaAsync(schema);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = schema;
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .PublishEventsToWolverine("Publish", x =>
                    {
                        x.PublishEvent<PcAEvent>();
                        x.PublishEvent<PcDEvent>((e, bus) => bus.PublishAsync(new PcTransformedMessage('D')));
                    });

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        Func<IMessageContext, Task> writeEvents = async _ =>
        {
            await using var session = store.LightweightSession();
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcCEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcDEvent(), new PcAEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcBEvent(), new PcBEvent(), new PcBEvent(), new PcEvent1(Guid.NewGuid()));

            await session.SaveChangesAsync();

            await daemon.WaitForNonStaleData(30.Seconds());
        };

        var tracked = await host
            .TrackActivity()
            .ExecuteAndWaitAsync(writeEvents);

        tracked.Executed.MessagesOf<IEvent<PcAEvent>>().Count().ShouldBe(6);

        // Filtered out
        tracked.Executed.MessagesOf<PcBEvent>().Count().ShouldBe(0);
        tracked.Executed.MessagesOf<IEvent<PcDEvent>>().Count().ShouldBe(0);
        tracked.Executed.MessagesOf<PcTransformedMessage>().Count().ShouldBe(6);
    }

    [Fact]
    public async Task using_singleton_scoped_subscription_from_service()
    {
        const string schema = "pc_subscriptions_singleton";
        await PreCleanSchemaAsync(schema);
        PcTaggingService.InstanceCount = 0;
        PcServiceUsingSubscription.Read.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton<PcTaggingService>();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = schema;
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .AddAsyncDaemon(DaemonMode.Solo)
                    .SubscribeToEventsWithServices<PcServiceUsingSubscription>(ServiceLifetime.Singleton);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        var daemon = host.Services.GetRequiredService<PolecatDaemonHostedService>().Daemon!;

        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
        session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());

        await session.SaveChangesAsync();

        await daemon.WaitForNonStaleData(20.Seconds());

        // Second round
        session.Events.StartStream(Guid.NewGuid(), new PcDEvent(), new PcDEvent(), new PcDEvent(), new PcDEvent());
        await session.SaveChangesAsync();

        PcServiceUsingSubscription.Read.Count().ShouldBe(1);
        PcServiceUsingSubscription.Read[1].OfType<PcAEvent>().Count().ShouldBe(5);
        PcServiceUsingSubscription.Read[1].OfType<PcBEvent>().Count().ShouldBe(3);
    }

    [Fact]
    public async Task using_scoped_subscription_from_service()
    {
        const string schema = "pc_subscriptions_scoped";
        await PreCleanSchemaAsync(schema);
        PcTaggingService.InstanceCount = 0;
        PcServiceUsingSubscription.Read.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Purposely using scoped here
                opts.Services.AddScoped<PcTaggingService>();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = schema;
                    }).IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .AddAsyncDaemon(DaemonMode.Solo)
                    .SubscribeToEventsWithServices<PcServiceUsingSubscription>(ServiceLifetime.Scoped);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        var daemon = host.Services.GetRequiredService<PolecatDaemonHostedService>().Daemon!;

        await using var session = store.LightweightSession();

        // Rigging this up so the daemon will have to use multiple batches
        for (int i = 0; i < 50; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcBEvent(), new PcDEvent(), new PcDEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcAEvent(), new PcAEvent(), new PcAEvent(), new PcAEvent());
            session.Events.StartStream(Guid.NewGuid(), new PcBEvent(), new PcCEvent(), new PcCEvent(), new PcBEvent());

            await session.SaveChangesAsync();
        }

        await daemon.WaitForNonStaleData(60.Seconds());

        PcServiceUsingSubscription.Read.Count().ShouldBeGreaterThan(1);
    }
}

public record PcEvent1(Guid Id);

public class PcTestBatchSubscription : BatchSubscription
{
    public PcTestBatchSubscription() : base("PcBatchTest")
    {
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var totals = await operations
            .Query<PcEventTotals>()
            .ToListAsync(cancellationToken);

        var cache = new LightweightCache<string, PcEventTotals>(letter => new PcEventTotals { Id = letter });

        foreach (var total in totals)
        {
            cache[total.Id] = total;
        }

        foreach (var e in page.Events)
        {
            if (e.Data is PcAEvent)
            {
                cache["A"].Count++;
            }

            if (e.Data is PcBEvent)
            {
                cache["B"].Count++;
            }

            if (e.Data is PcCEvent)
            {
                cache["C"].Count++;
            }

            if (e.Data is PcDEvent)
            {
                cache["D"].Count++;
            }
        }

        foreach (var total in cache)
        {
            operations.Store(total);
            await bus.PublishAsync(new PcEventTotalsUpdated(total));
        }
    }
}

public class PcEventTotals
{
    public string Id { get; set; }
    public int Count { get; set; }
}

public record PcEventTotalsUpdated(PcEventTotals totals);

public static class PcEventTotalsUpdatedHandler
{
    public static void Handle(PcEventTotalsUpdated updated)
    {
        // don't need to do anything
    }
}

public record PcTransformedMessage(char Letter);

public static class PcTotalsHandler
{
    public static List<char> Handled { get; } = new();

    public static void Handle(PcTransformedMessage message)
    {
    }

    public static void Handle(IEvent<PcAEvent> e)
    {
        Handled.Add('a');
    }

    public static void Handle(PcBEvent e)
    {
        Handled.Add('b');
    }

    public static void Handle(PcCEvent e)
    {
        Handled.Add('c');
    }

    public static void Handle(IEvent<PcDEvent> e)
    {
        Handled.Add('d');
    }
}

public class PcServiceUsingSubscription : BatchSubscription
{
    private readonly PcTaggingService _service;

    public static LightweightCache<int, List<object>> Read { get; } = new(i => new());

    public PcServiceUsingSubscription(PcTaggingService service) : base("PcTagged")
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

public class PcTaggingService
{
    public static int InstanceCount = 0;

    public PcTaggingService()
    {
        Instance = ++InstanceCount;
    }

    public int Instance { get; set; }
}

public record PcAEvent;

public record PcBEvent;

public record PcCEvent;

public record PcDEvent;
