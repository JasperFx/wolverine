using JasperFx;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

public record StartRevisionedComplianceSaga(Guid Id);

public record TouchRevisionedComplianceSaga(Guid Id);

public record FinishRevisionedComplianceSaga(Guid Id);

// A saga that opts into numeric revisions. Saga.Version is the IRevisioned.Version
public class RevisionedComplianceSaga : Saga, IRevisioned
{
    public Guid Id { get; set; }
    public int Touches { get; set; }

    public static RevisionedComplianceSaga Start(StartRevisionedComplianceSaga command) => new() { Id = command.Id };

    public void Handle(TouchRevisionedComplianceSaga command) => Touches++;

    public void Handle(FinishRevisionedComplianceSaga command) => MarkCompleted();
}

/// <summary>
/// GH-5658. A saga that implements <see cref="IRevisioned" /> is saved at the revision after the one it was
/// loaded with, so every saga storage on a document store that honours <see cref="IRevisioned" /> can update it.
/// </summary>
public abstract class RevisionedSagaCompliance : IAsyncLifetime
{
    private static readonly string SagaTypeName = typeof(RevisionedComplianceSaga).FullName!;

    protected IHost Host { get; private set; } = null!;

    /// <summary>
    /// Configure the saga storage under test. The handler discovery is already restricted to
    /// <see cref="RevisionedComplianceSaga" />, and the host runs in Solo mode.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RevisionedComplianceSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
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

    private ISagaStoreDiagnostics sagas => Host.GetRuntime().SagaStorage;

    private async Task<int?> readTouchesAsync(Guid id)
    {
        var state = await sagas.ReadSagaAsync(SagaTypeName, id, TestContext.Current.CancellationToken);
        if (state == null) return null;

        foreach (var property in state.State.EnumerateObject())
        {
            if (property.Name.Equals(nameof(RevisionedComplianceSaga.Touches), StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetInt32();
            }
        }

        return 0;
    }

    [Fact]
    public async Task a_revisioned_saga_can_be_updated_more_than_once()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartRevisionedComplianceSaga(id));
        await Host.InvokeMessageAndWaitAsync(new TouchRevisionedComplianceSaga(id));
        await Host.InvokeMessageAndWaitAsync(new TouchRevisionedComplianceSaga(id));
        await Host.InvokeMessageAndWaitAsync(new TouchRevisionedComplianceSaga(id));

        (await readTouchesAsync(id)).ShouldBe(3);
    }

    [Fact]
    public async Task a_revisioned_saga_can_be_completed_after_an_update()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartRevisionedComplianceSaga(id));
        await Host.InvokeMessageAndWaitAsync(new TouchRevisionedComplianceSaga(id));
        await Host.InvokeMessageAndWaitAsync(new FinishRevisionedComplianceSaga(id));

        (await readTouchesAsync(id)).ShouldBeNull();
    }
}
