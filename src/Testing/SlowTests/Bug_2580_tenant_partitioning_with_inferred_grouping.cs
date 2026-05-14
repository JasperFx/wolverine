using System.Collections.Concurrent;
using System.Threading;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using JasperFx.Events.Projections;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Xunit;

namespace SlowTests;

public class Bug_2580_tenant_partitioning_with_inferred_grouping
{
    [Fact]
    public async Task issue_2580_same_tenant_messages_should_not_run_in_parallel_when_by_tenant_id_is_configured()
    {
        const string tenantId = "red";
        var schemaName = "tenant_partitioning_" + Guid.NewGuid().ToString("N");
        var aggregateIds = Enumerable.Range(0, 18).Select(_ => Guid.NewGuid()).ToArray();
        var tracker = new TenantPartitioningConcurrencyTracker(aggregateIds.Length);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TenantPartitionedAggregateHandler));

                opts.Services.AddSingleton(tracker);
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = schemaName;
                        m.DisableNpgsqlLogging = true;
                        m.Projections.Snapshot<TenantPartitionedAggregate>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
                opts.Policies.AutoApplyTransactions();

                opts.MessagePartitioning
                    .UseInferredMessageGrouping()
                    .ByTenantId()
                    .PublishToPartitionedLocalMessaging("tenant-check", 3, topology =>
                    {
                        topology.Message<TenantPartitioningProbe>();
                        topology.MaxDegreeOfParallelism = PartitionSlots.Three;
                        topology.ConfigureQueues(x => x.BufferedInMemory());
                    });
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            foreach (var aggregateId in aggregateIds)
            {
                session.Events.StartStream<TenantPartitionedAggregate>(aggregateId, new TenantPartitioningStarted());
            }

            await session.SaveChangesAsync();
        }

        var bus = host.Services.GetRequiredService<IMessageBus>();
        await Task.WhenAll(aggregateIds.Select(id =>
            bus.PublishAsync(new TenantPartitioningProbe(id), new DeliveryOptions { TenantId = tenantId }).AsTask()));

        await tracker.WaitForCompletionAsync(30.Seconds());

        tracker.MaxConcurrency.ShouldBe(1,
            $"Issue #2580 repro: expected messages for tenant '{tenantId}' to run sequentially because ByTenantId() is configured, " +
            $"but observed max concurrency {tracker.MaxConcurrency}. " +
            $"Observed GroupIds: {string.Join(", ", tracker.GroupIds.OrderBy(x => x).Distinct().Take(10))}");
    }
}

public record TenantPartitioningProbe(Guid TenantPartitionedAggregateId);

public record TenantPartitioningStarted;

public record TenantPartitioningApplied;

public class TenantPartitionedAggregate
{
    public Guid Id { get; set; }

    public TenantPartitionedAggregate()
    {
    }

    public TenantPartitionedAggregate(TenantPartitioningStarted _)
    {
    }

    public void Apply(TenantPartitioningApplied _)
    {
    }
}

[AggregateHandler]
public static class TenantPartitionedAggregateHandler
{
    public static async Task<TenantPartitioningApplied> Handle(
        TenantPartitioningProbe command,
        TenantPartitionedAggregate aggregate,
        Envelope envelope,
        TenantPartitioningConcurrencyTracker tracker)
    {
        tracker.Start(envelope);

        try
        {
            await Task.Delay(250.Milliseconds());
            return new TenantPartitioningApplied();
        }
        finally
        {
            tracker.Finish();
        }
    }
}

public class TenantPartitioningConcurrencyTracker
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int _expectedMessages;
    private int _active;
    private int _completed;
    private int _maxConcurrency;

    public TenantPartitioningConcurrencyTracker(int expectedMessages)
    {
        _expectedMessages = expectedMessages;
    }

    public ConcurrentBag<string> GroupIds { get; } = new();

    public int MaxConcurrency => _maxConcurrency;

    public void Start(Envelope envelope)
    {
        GroupIds.Add(envelope.GroupId ?? "<null>");

        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var current = _maxConcurrency;
            if (active <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxConcurrency, active, current) == current)
            {
                return;
            }
        }
    }

    public void Finish()
    {
        Interlocked.Decrement(ref _active);

        if (Interlocked.Increment(ref _completed) >= _expectedMessages)
        {
            _completion.TrySetResult(true);
        }
    }

    public async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        await _completion.Task.WaitAsync(timeout);
    }
}
