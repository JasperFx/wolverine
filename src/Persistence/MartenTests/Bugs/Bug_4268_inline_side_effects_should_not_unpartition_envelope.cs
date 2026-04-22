using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_4268_inline_side_effects_should_not_unpartition_envelope
{
    private const string SchemaName = "bug4268";

    [Fact]
    public async Task inline_projection_side_effects_should_not_try_to_remove_tenant_id_from_envelope_storage()
    {
        await DropSchemasAsync();

        // Step 1: build the schema under async projections. Even though the
        // ancillary store applies AllDocumentsAreMultiTenantedWithPartitioning
        // to every document, Wolverine's Envelope outbox table must be exempt —
        // otherwise two stores sharing a schema drift apart on its shape and
        // the next schema diff emits an impossible "drop partitioning column"
        // migration. See GH-2566 / marten#4268.
        await BuildOriginalAsyncProjectionStorageAsync();
        (await EnvelopeStorageIsTenantPartitionedAsync()).ShouldBeFalse(
            "Envelope storage should stay single-tenant / unpartitioned regardless of the store's blanket AllDocumentsAreMultiTenantedWithPartitioning policy");

        // Step 2: flip to inline projections + enable side effects. Without the
        // fix this threw Marten.Exceptions.MartenSchemaException wrapping
        // "unique constraint on partitioned table must include all partitioning
        // columns" on the emitted "alter table ... drop column tenant_id" DDL.
        var exception = await Record.ExceptionAsync(TriggerInlineProjectionSideEffectAsync);

        if (exception is not null)
        {
            exception.ToString().ShouldNotContain("drop column tenant_id");
        }

        exception.ShouldBeNull();
    }

    private static async Task BuildOriginalAsyncProjectionStorageAsync()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableInboxPartitioning = true;

                ConfigureMainStore(opts, enableInlineSideEffects: false);

                opts.Services.AddMartenStore<IBug4268Store>(_ =>
                    {
                        var m = new StoreOptions();
                        ConfigureAncillaryStore(m);
                        return m;
                    })
                    .AddProjectionWithServices<Bug4268Projection>(ProjectionLifecycle.Async, ServiceLifetime.Singleton)
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.Policies.UseDurableLocalQueues();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug4268SideEffectHandler));
            }).StartAsync();

        var streamId = Guid.NewGuid();
        var store = host.Services.GetRequiredService<IBug4268Store>();

        await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<Bug4268SideEffect>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var session = store.LightweightSession("tenant1");
                session.Events.StartStream<Bug4268Aggregate>(streamId, new Bug4268Started());
                await session.SaveChangesAsync();
            }));
    }

    private static async Task TriggerInlineProjectionSideEffectAsync()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableInboxPartitioning = true;

                ConfigureMainStore(opts, enableInlineSideEffects: true);

                opts.Services.AddMartenStore<IBug4268Store>(_ =>
                    {
                        var m = new StoreOptions();
                        ConfigureAncillaryStore(m);
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        return m;
                    })
                    .AddProjectionWithServices<Bug4268Projection>(ProjectionLifecycle.Inline, ServiceLifetime.Singleton)
                    .IntegrateWithWolverine();

                opts.Policies.UseDurableLocalQueues();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug4268SideEffectHandler));
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession("tenant1");
        session.Events.StartStream<Bug4268MainAggregate>(Guid.NewGuid(), new Bug4268MainStarted());
        await session.SaveChangesAsync();
    }

    private static void ConfigureMainStore(WolverineOptions opts, bool enableInlineSideEffects)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = SchemaName;
            m.Events.DatabaseSchemaName = SchemaName;
            m.Events.TenancyStyle = TenancyStyle.Conjoined;
            m.Advanced.DefaultTenantUsageEnabled = false;
            m.Schema.For<Bug4268MainAggregate>().MultiTenanted();

            if (enableInlineSideEffects)
            {
                m.Events.EnableSideEffectsOnInlineProjections = true;
                m.Projections.Add<Bug4268MainProjection>(ProjectionLifecycle.Inline);
            }

            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
    }

    private static void ConfigureAncillaryStore(StoreOptions m)
    {
        m.Connection(Servers.PostgresConnectionString);
        m.DatabaseSchemaName = SchemaName;
        m.Events.DatabaseSchemaName = SchemaName;
        m.Events.TenancyStyle = TenancyStyle.Conjoined;
        m.Advanced.DefaultTenantUsageEnabled = false;

        // Do not configure Envelope directly. The existing envelope storage shape
        // comes from the ancillary store's normal multi-tenanted document policy.
        m.Policies.AllDocumentsAreMultiTenantedWithPartitioning(x =>
        {
            x.ByHash("one", "two");
        });
        m.DisableNpgsqlLogging = true;
    }

    private static async Task<bool> EnvelopeStorageIsTenantPartitionedAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          select c.relkind = 'p'
                          from pg_class c
                          join pg_namespace n on n.oid = c.relnamespace
                          join information_schema.columns col on col.table_schema = n.nspname and col.table_name = c.relname
                          where n.nspname = @schema
                            and c.relname = 'mt_doc_envelope'
                            and col.column_name = 'tenant_id'
                          """;
        cmd.Parameters.AddWithValue("schema", SchemaName);

        return await cmd.ExecuteScalarAsync() as bool? == true;
    }

    private static async Task DropSchemasAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
    }
}

public interface IBug4268Store : IDocumentStore;

public record Bug4268Started;

public record Bug4268MainStarted;

public record Bug4268SideEffect(Guid StreamId);

public class Bug4268Aggregate
{
    public Guid Id { get; set; }

    public static Bug4268Aggregate Create(Bug4268Started _) => new();
}

public class Bug4268MainAggregate
{
    public Guid Id { get; set; }

    public static Bug4268MainAggregate Create(Bug4268MainStarted _) => new();
}

public class Bug4268Projection : SingleStreamProjection<Bug4268Aggregate, Guid>
{
    public static Bug4268Aggregate Create(Bug4268Started _) => new();

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Bug4268Aggregate> slice)
    {
        if (slice.Snapshot is not null)
        {
            slice.PublishMessage(new Bug4268SideEffect(slice.Snapshot.Id));
        }

        return ValueTask.CompletedTask;
    }
}

public class Bug4268MainProjection : SingleStreamProjection<Bug4268MainAggregate, Guid>
{
    public static Bug4268MainAggregate Create(Bug4268MainStarted _) => new();

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Bug4268MainAggregate> slice)
    {
        if (slice.Snapshot is not null)
        {
            slice.PublishMessage(new Bug4268SideEffect(slice.Snapshot.Id));
        }

        return ValueTask.CompletedTask;
    }
}

public static class Bug4268SideEffectHandler
{
    public static void Handle(Bug4268SideEffect _)
    {
    }
}
