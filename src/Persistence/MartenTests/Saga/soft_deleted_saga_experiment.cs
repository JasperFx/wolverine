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

        // Normal load should NOT find the soft-deleted saga
        await using var session2 = _host.DocumentStore().QuerySession();
        var afterComplete = await session2.LoadAsync<SoftDeletedOrderSaga>(id);
        afterComplete.ShouldBeNull();

        // But with MaybeDeleted, we should still be able to find it
        var includingDeleted = await session2
            .Query<SoftDeletedOrderSaga>()
            .Where(x => x.Id == id)
            .Where(x => x.MaybeDeleted())
            .FirstOrDefaultAsync();
        includingDeleted.ShouldNotBeNull();
        includingDeleted.ProductName.ShouldBe("Widget");
    }

    [Fact]
    public async Task send_message_to_completed_soft_deleted_saga()
    {
        var id = Guid.NewGuid();

        // Start the saga
        await _host.SendMessageAndWaitAsync(new StartSoftDeleteOrder(id, "Gadget"));

        // Complete the saga
        await _host.SendMessageAndWaitAsync(new CompleteSoftDeleteOrder(id));

        // Now send another message targeting the completed (soft-deleted) saga
        // What happens? Does Wolverine find it or treat it as not found?
        await _host.SendMessageAndWaitAsync(new PokeSoftDeleteOrder(id));

        // Check if the saga was somehow resurrected or if it stayed deleted
        await using var session = _host.DocumentStore().QuerySession();

        // Normal load
        var normalLoad = await session.LoadAsync<SoftDeletedOrderSaga>(id);

        // Load including deleted
        var withDeleted = await session
            .Query<SoftDeletedOrderSaga>()
            .Where(x => x.Id == id)
            .Where(x => x.MaybeDeleted())
            .FirstOrDefaultAsync();

        // Report findings
        if (normalLoad != null)
        {
            // Saga was resurrected - the soft-deleted document was found and updated
            throw new Exception($"FINDING: Saga was RESURRECTED after sending message to soft-deleted saga. " +
                                $"WasHandled={withDeleted?.WasHandledAfterCompletion}");
        }
        else if (withDeleted?.WasHandledAfterCompletion == true)
        {
            // Saga was found (soft-deleted), handler ran, but it's still soft-deleted
            throw new Exception("FINDING: Handler ran on the soft-deleted saga but it stayed deleted");
        }
        else
        {
            // Saga was NOT found - Wolverine correctly treats soft-deleted as not-found
            // This is the expected/desired behavior
            normalLoad.ShouldBeNull();
        }
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
