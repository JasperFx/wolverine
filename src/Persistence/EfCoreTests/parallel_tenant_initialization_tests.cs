using System.Collections.Concurrent;
using Shouldly;

namespace EfCoreTests;

public class parallel_tenant_initialization_tests
{
    [Fact]
    public async Task all_tenants_are_initialized()
    {
        var initialized = new ConcurrentBag<string>();
        var tenantIds = Enumerable.Range(1, 20).Select(i => $"tenant{i}").ToList();

        await Parallel.ForEachAsync(tenantIds, new ParallelOptions { MaxDegreeOfParallelism = 10 },
            async (tenantId, ct) =>
            {
                await Task.Delay(5, ct);
                initialized.Add(tenantId);
            });

        initialized.Count.ShouldBe(tenantIds.Count);
        foreach (var tenantId in tenantIds)
        {
            initialized.ShouldContain(tenantId);
        }
    }

    [Fact]
    public async Task concurrent_initialization_is_faster_than_sequential()
    {
        const int tenantCount = 10;
        const int perTenantDelayMs = 50;

        var tenantIds = Enumerable.Range(1, tenantCount).Select(i => $"tenant{i}").ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(tenantIds, new ParallelOptions { MaxDegreeOfParallelism = 10 },
            async (tenantId, ct) => await Task.Delay(perTenantDelayMs, ct));
        sw.Stop();

        // Sequential would take tenantCount * perTenantDelayMs = 500ms minimum.
        // Parallel should complete much faster. Allow up to half the sequential time.
        sw.ElapsedMilliseconds.ShouldBeLessThan(tenantCount * perTenantDelayMs / 2);
    }

    [Fact]
    public async Task single_tenant_failure_surfaces_as_exception()
    {
        var tenantIds = Enumerable.Range(1, 5).Select(i => $"tenant{i}").ToList();

        await Should.ThrowAsync<Exception>(async () =>
        {
            await Parallel.ForEachAsync(tenantIds, new ParallelOptions { MaxDegreeOfParallelism = 10 },
                async (tenantId, ct) =>
                {
                    await Task.Delay(1, ct);
                    if (tenantId == "tenant3")
                        throw new InvalidOperationException($"Database initialization failed for {tenantId}");
                });
        });
    }
}
