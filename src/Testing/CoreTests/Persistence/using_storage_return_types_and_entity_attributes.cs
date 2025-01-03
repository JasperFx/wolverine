using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Sagas;

namespace CoreTests.Persistence;

public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        // Nothing, just use the in memory persistor
    }

    public override Task<Todo?> Load(Guid id)
    {
        return Task.FromResult(Host.Services.GetRequiredService<InMemorySagaPersistor>().Load<Todo>(id));
    }

    public override Task Persist(Todo todo)
    {
        Host.Services.GetRequiredService<InMemorySagaPersistor>().Store(todo);
        return Task.CompletedTask;
    }
}