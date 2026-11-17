using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

/// <summary>
///     GH-5345. A saga written through a storage side effect instead of a saga chain still gets
///     <see cref="Wolverine.Saga.LastModified" />, over any value the handler put there.
/// </summary>
public class saga_last_modified_through_storage_actions : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.DisableConventionalDiscovery().IncludeType(typeof(StorageActionSagaHandler));
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task shouldBeStamped(object message, Guid id)
    {
        var before = DateTimeOffset.UtcNow;

        await _host.InvokeMessageAndWaitAsync(message);

        var saga = _host.Services.GetRequiredService<InMemorySagaPersistor>().Load<StorageActionSaga>(id);
        saga.ShouldNotBeNull();
        saga.LastModified.ShouldNotBe(StorageActionSagaHandler.Backdated);
        saga.LastModified.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public async Task insert()
    {
        var id = Guid.NewGuid();
        await shouldBeStamped(new InsertStorageActionSaga(id), id);
    }

    [Fact]
    public async Task update()
    {
        var id = Guid.NewGuid();
        await shouldBeStamped(new UpdateStorageActionSaga(id), id);
    }

    [Fact]
    public async Task store()
    {
        var id = Guid.NewGuid();
        await shouldBeStamped(new StoreStorageActionSaga(id), id);
    }

    [Fact]
    public async Task storage_action()
    {
        var id = Guid.NewGuid();
        await shouldBeStamped(new StorageActionOnSaga(id), id);
    }

    [Fact]
    public async Task unit_of_work()
    {
        var id = Guid.NewGuid();
        await shouldBeStamped(new UnitOfWorkOnSaga(id), id);
    }
}

public record InsertStorageActionSaga(Guid Id);
public record UpdateStorageActionSaga(Guid Id);
public record StoreStorageActionSaga(Guid Id);
public record StorageActionOnSaga(Guid Id);
public record UnitOfWorkOnSaga(Guid Id);

public class StorageActionSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
}

public static class StorageActionSagaHandler
{
    public static readonly DateTimeOffset Backdated = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StorageActionSaga backdated(Guid id) => new() { Id = id, LastModified = Backdated };

    public static Insert<StorageActionSaga> Handle(InsertStorageActionSaga command)
        => Storage.Insert(backdated(command.Id));

    public static Update<StorageActionSaga> Handle(UpdateStorageActionSaga command)
        => Storage.Update(backdated(command.Id));

    public static Store<StorageActionSaga> Handle(StoreStorageActionSaga command)
        => Storage.Store(backdated(command.Id));

    public static IStorageAction<StorageActionSaga> Handle(StorageActionOnSaga command)
        => Storage.Insert(backdated(command.Id));

    public static UnitOfWork<StorageActionSaga> Handle(UnitOfWorkOnSaga command)
    {
        var work = new UnitOfWork<StorageActionSaga>();
        work.Insert(backdated(command.Id));
        return work;
    }
}
