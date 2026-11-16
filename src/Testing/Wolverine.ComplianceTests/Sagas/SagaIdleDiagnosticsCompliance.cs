using JasperFx.Core;
using JasperFx.Documents;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

public record StartIdleDiagnosticsSaga(Guid Id, string Name);

public record TouchIdleDiagnosticsSaga(Guid Id);

public class IdleDiagnosticsSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Touches { get; set; }

    public static IdleDiagnosticsSaga Start(StartIdleDiagnosticsSaga command)
    {
        return new IdleDiagnosticsSaga { Id = command.Id, Name = command.Name };
    }

    public void Handle(TouchIdleDiagnosticsSaga command)
    {
        Touches++;
    }
}

/// <summary>
/// GH-5558. What every saga storage that implements <see cref="ISagaIdleDiagnostics" /> must do, against a real host.
/// Every assertion is a range that starts at a timestamp taken during the test, so instances left over from earlier
/// runs (all older than that) never change the answer.
/// </summary>
public abstract class SagaIdleDiagnosticsCompliance : IAsyncLifetime
{
    private static readonly TimeSpan Gap = 300.Milliseconds();
    private static readonly string SagaTypeName = typeof(IdleDiagnosticsSaga).FullName!;

    protected IHost Host { get; private set; } = null!;

    /// <summary>
    /// Configure the saga storage under test. The handler discovery is already restricted to
    /// <see cref="IdleDiagnosticsSaga" />.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<IdleDiagnosticsSaga>();
                ConfigureStorage(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        await CleanUpAsync();
    }

    /// <summary>Release anything <see cref="ConfigureStorage" /> created, such as a SQLite database file.</summary>
    protected virtual ValueTask CleanUpAsync() => ValueTask.CompletedTask;

    private ISagaIdleDiagnostics idle => Host.GetRuntime().SagaStorage.ShouldBeAssignableTo<ISagaIdleDiagnostics>()!;

    private async Task<Guid[]> startAsync(int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            await Host.InvokeMessageAndWaitAsync(new StartIdleDiagnosticsSaga(id, "saga-" + i));
            ids.Add(id);

            // Keeps the instances in a known last-modified order
            await Task.Delay(20.Milliseconds(), TestContext.Current.CancellationToken);
        }

        return ids.ToArray();
    }

    private static async Task<DateTimeOffset> markAsync()
    {
        await Task.Delay(Gap, TestContext.Current.CancellationToken);
        var mark = DateTimeOffset.UtcNow;
        await Task.Delay(Gap, TestContext.Current.CancellationToken);
        return mark;
    }

    private static Guid[] idsOf(SagaQueryResult result)
    {
        return result.Instances.Select(x => Guid.Parse(x.Identity.ToString()!)).ToArray();
    }

    [Fact]
    public void the_storage_supports_idle_queries_for_its_saga_type()
    {
        idle.SupportsIdleQueries(SagaTypeName).ShouldBeTrue();
        idle.SupportsIdleQueries(nameof(IdleDiagnosticsSaga)).ShouldBeTrue();
        idle.SupportsIdleQueries("NotASagaAnyoneOwns").ShouldBeFalse();
    }

    [Fact]
    public async Task count_and_query_sagas_by_last_modified()
    {
        var start = await markAsync();
        var older = await startAsync(3);
        var cutoff = await markAsync();
        var newer = await startAsync(2);
        var end = await markAsync();

        var token = TestContext.Current.CancellationToken;

        // Counted by difference, so instances from earlier runs (all older than start) cancel out
        var beforeStart = await idle.CountIdleSagasAsync(SagaTypeName, start, null, token);
        (await idle.CountIdleSagasAsync(SagaTypeName, cutoff, null, token) - beforeStart).ShouldBe(3);
        (await idle.CountIdleSagasAsync(SagaTypeName, end, null, token) - beforeStart).ShouldBe(5);

        // The short type name works too
        (await idle.CountIdleSagasAsync(nameof(IdleDiagnosticsSaga), cutoff, null, token) - beforeStart).ShouldBe(3);

        var idleOnes = await idle.QuerySagasByLastModifiedAsync(SagaTypeName,
            new SagaLastModifiedRange(start, cutoff), new DocumentQueryOptions(1, 50), token);
        idleOnes.TotalCount.ShouldBe(3);

        // Least recently modified first
        idsOf(idleOnes).ShouldBe(older);

        var recent = await idle.QuerySagasByLastModifiedAsync(SagaTypeName, SagaLastModifiedRange.After(cutoff),
            new DocumentQueryOptions(1, 50), token);
        idsOf(recent).ShouldBe(newer);
        recent.TotalCount.ShouldBe(2);

        // Paged
        var page = await idle.QuerySagasByLastModifiedAsync(SagaTypeName, new SagaLastModifiedRange(start, end),
            new DocumentQueryOptions(2, 2), token);
        page.TotalCount.ShouldBe(5);
        idsOf(page).ShouldBe([older[2], newer[0]]);

        // Writing a saga again makes it recent
        await Host.InvokeMessageAndWaitAsync(new TouchIdleDiagnosticsSaga(older[0]));
        (await idle.CountIdleSagasAsync(SagaTypeName, cutoff, null, token) - beforeStart).ShouldBe(2);
        idsOf(await idle.QuerySagasByLastModifiedAsync(SagaTypeName, SagaLastModifiedRange.After(cutoff),
            new DocumentQueryOptions(1, 50), token)).ShouldBe([..newer, older[0]]);
    }

    [Fact]
    public async Task an_unknown_saga_type_counts_zero_and_reads_empty()
    {
        var token = TestContext.Current.CancellationToken;
        (await idle.CountIdleSagasAsync("NotASagaAnyoneOwns", DateTimeOffset.UtcNow, null, token)).ShouldBe(0);

        var result = await idle.QuerySagasByLastModifiedAsync("NotASagaAnyoneOwns", SagaLastModifiedRange.Any,
            new DocumentQueryOptions(1, 10), token);
        result.Instances.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_tenant_the_storage_cannot_scope_to_is_refused()
    {
        var token = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            idle.CountIdleSagasAsync(SagaTypeName, DateTimeOffset.UtcNow, "red", token));

        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            idle.QuerySagasByLastModifiedAsync(SagaTypeName, SagaLastModifiedRange.Any,
                new DocumentQueryOptions(1, 10) { TenantId = "red" }, token));
    }
}
