using IntegrationTests;
using Marten;
using Marten.Linq.SoftDeletes;
using Marten.Schema;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class soft_deleted_saga_experiment : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<SoftDeletedOrderSaga>();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "soft_delete_saga";

                    // Configure the saga type to use soft deletes in Marten
                    m.Schema.For<SoftDeletedOrderSaga>().SoftDeleted();
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task saga_is_soft_deleted_when_completed()
    {
        // KNOWN BEHAVIOR: Marten's LoadAsync() does NOT filter soft-deleted documents.
        // Only LINQ queries apply the soft-delete filter. So after MarkCompleted()
        // triggers session.Delete(), the saga is soft-deleted in the database but
        // LoadAsync still returns it. LINQ queries without MaybeDeleted() will
        // correctly filter it out.

        var id = Guid.NewGuid();

        // Start the saga
        await _host.SendMessageAndWaitAsync(new StartSoftDeleteOrder(id, "Widget"));

        await using var session = _host.DocumentStore().QuerySession();

        // Verify saga exists
        var saga = await session.LoadAsync<SoftDeletedOrderSaga>(id);
        saga.ShouldNotBeNull();
        saga.ProductName.ShouldBe("Widget");

        // Complete the saga (this calls MarkCompleted() which triggers Delete)
        await _host.SendMessageAndWaitAsync(new CompleteSoftDeleteOrder(id));

        // LoadAsync does NOT filter soft-deleted documents — this is standard Marten behavior
        await using var session2 = _host.DocumentStore().QuerySession();
        var afterComplete = await session2.LoadAsync<SoftDeletedOrderSaga>(id);
        afterComplete.ShouldNotBeNull("LoadAsync returns soft-deleted documents");

        // But a LINQ query WITHOUT MaybeDeleted() filters the soft-deleted saga out
        var filteredQuery = await session2
            .Query<SoftDeletedOrderSaga>()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync();
        filteredQuery.ShouldBeNull("LINQ queries filter soft-deleted documents by default");

        // With MaybeDeleted(), we can still find the soft-deleted saga
        var includingDeleted = await session2
            .Query<SoftDeletedOrderSaga>()
            .Where(x => x.Id == id)
            .Where(x => x.MaybeDeleted())
            .FirstOrDefaultAsync();
        includingDeleted.ShouldNotBeNull();
        includingDeleted.ProductName.ShouldBe("Widget");
    }

    [Fact]
    public async Task send_message_to_completed_soft_deleted_saga_resurrects_it()
    {
        // KNOWN BEHAVIOR: Wolverine uses LoadAsync() to find sagas, which does NOT
        // filter out soft-deleted documents. This means sending a message to a
        // soft-deleted saga will "resurrect" it — the handler runs and the document
        // is updated back to a non-deleted state.
        //
        // Recommendation: Use ISoftDeleted interface on your saga class and guard
        // against processing in handlers by checking the Deleted property.
        // See docs/guide/durability/marten/sagas.md for details.

        var id = Guid.NewGuid();

        // Start the saga
        await _host.SendMessageAndWaitAsync(new StartSoftDeleteOrder(id, "Gadget"));

        // Complete the saga
        await _host.SendMessageAndWaitAsync(new CompleteSoftDeleteOrder(id));

        // Now send another message targeting the completed (soft-deleted) saga
        await _host.SendMessageAndWaitAsync(new PokeSoftDeleteOrder(id));

        await using var session = _host.DocumentStore().QuerySession();

        // The saga is resurrected — LoadAsync finds soft-deleted docs, and the
        // handler updates the document, removing the soft-delete marker
        var normalLoad = await session.LoadAsync<SoftDeletedOrderSaga>(id);
        normalLoad.ShouldNotBeNull("Saga should be resurrected after receiving a message");
        normalLoad.WasHandledAfterCompletion.ShouldBeTrue();
    }
}

// Messages
public record StartSoftDeleteOrder(Guid SoftDeletedOrderSagaId, string ProductName);
public record CompleteSoftDeleteOrder(Guid SoftDeletedOrderSagaId);
public record PokeSoftDeleteOrder(Guid SoftDeletedOrderSagaId);

// Saga with soft-delete configured via Marten
[SoftDeleted]
[WolverineIgnore]
public class SoftDeletedOrderSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public bool WasHandledAfterCompletion { get; set; }

    public static SoftDeletedOrderSaga Start(StartSoftDeleteOrder message)
    {
        return new SoftDeletedOrderSaga
        {
            Id = message.SoftDeletedOrderSagaId,
            ProductName = message.ProductName
        };
    }

    public void Handle(CompleteSoftDeleteOrder message)
    {
        MarkCompleted();
    }

    public void Handle(PokeSoftDeleteOrder message)
    {
        // If this handler is called on a soft-deleted saga, record it
        WasHandledAfterCompletion = true;
    }
}
