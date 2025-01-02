using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence;

public class using_storage_return_types : IntegrationContext
{
    /*
     * Store
     * Insert
     * Update
     * Delete
     * return the interface
     *
     * 
     */

    public using_storage_return_types(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task use_insert_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();

        var todo = persistor.Load<Todo>(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }
    
    [Fact]
    public async Task use_store_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();

        var todo = persistor.Load<Todo>(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }
}

public class Todo
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}

public record CreateTodo(Guid Id, string Name);
public record CreateTodo2(Guid Id, string Name);

public static class TodoHandler
{
    public static Insert<Todo> Handle(CreateTodo command) => Storage.Insert(new Todo
    {
        Id = command.Id,
        Name = command.Name
    });
    
    public static Store<Todo> Handle(CreateTodo2 command) => Storage.Store(new Todo
    {
        Id = command.Id,
        Name = command.Name
    });
}