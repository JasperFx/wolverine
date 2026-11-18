using IntegrationTests;
using JasperFx.Resources;
using Marten;
using JasperFx;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests.Saga;

/// <summary>
/// GH-5570. Marten 10 (marten#5906) checks the revision of a revisioned document passed to <c>Store()</c>: a non-zero
/// <c>Version</c> is the revision the document must become, so it has to be greater than the stored one. Wolverine maps
/// <see cref="Wolverine.Saga.Version" /> as the revision of every saga, and its own saga frames already store the next
/// revision. A handler that loads a saga itself and stores it again unchanged now fails.
/// </summary>
public class restoring_a_loaded_saga_5570 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RestoringSagaHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "restoring_sagas";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> insertSagaAsync()
    {
        var saga = new RestoredSaga { Id = Guid.NewGuid() };
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Insert(saga);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return saga.Id;
    }

    private async Task<RestoredSaga> loadAsync(Guid id)
    {
        await using var session = theHost.DocumentStore().QuerySession();
        return (await session.LoadAsync<RestoredSaga>(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task storing_a_loaded_saga_again_at_the_same_revision_throws()
    {
        var id = await insertSagaAsync();

        await Should.ThrowAsync<ConcurrencyException>(() =>
            theHost.MessageBus().InvokeAsync(new StoreSagaAsLoaded(id), TestContext.Current.CancellationToken));

        (await loadAsync(id)).Note.ShouldBeNull();
    }

    [Fact]
    public async Task storing_a_loaded_saga_at_the_next_revision_works()
    {
        var id = await insertSagaAsync();

        await theHost.MessageBus().InvokeAsync(new StoreSagaAtNextRevision(id), TestContext.Current.CancellationToken);

        var saga = await loadAsync(id);
        saga.Note.ShouldBe("next revision");
        saga.Version.ShouldBe(2);
    }

    [Fact]
    public async Task storing_a_loaded_saga_with_version_zero_skips_the_check()
    {
        var id = await insertSagaAsync();

        await theHost.MessageBus().InvokeAsync(new StoreSagaWithoutRevision(id), TestContext.Current.CancellationToken);

        (await loadAsync(id)).Note.ShouldBe("no revision");
    }
}

public class RestoredSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string? Note { get; set; }
}

public record StoreSagaAsLoaded(Guid Id);

public record StoreSagaAtNextRevision(Guid Id);

public record StoreSagaWithoutRevision(Guid Id);

public static class RestoringSagaHandler
{
    // Fails on Marten 10: the saga still carries the revision it was loaded at
    public static async Task<IMartenOp> Handle(StoreSagaAsLoaded command, IQuerySession session)
    {
        var saga = await session.LoadAsync<RestoredSaga>(command.Id);
        saga!.Note = "as loaded";
        return MartenOps.Store(saga);
    }

    // The remedy: store the next revision
    public static async Task<IMartenOp> Handle(StoreSagaAtNextRevision command, IQuerySession session)
    {
        var saga = await session.LoadAsync<RestoredSaga>(command.Id);
        saga!.Note = "next revision";
        saga.Version++;
        return MartenOps.Store(saga);
    }

    // Or opt out of the check
    public static async Task<IMartenOp> Handle(StoreSagaWithoutRevision command, IQuerySession session)
    {
        var saga = await session.LoadAsync<RestoredSaga>(command.Id);
        saga!.Note = "no revision";
        saga.Version = 0;
        return MartenOps.Store(saga);
    }
}
