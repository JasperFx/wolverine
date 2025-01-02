using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence;

public class using_storage_return_types_and_entity_attributes : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TodoHandler));
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
    }


    public IHost Host { get; set; }

    [Fact]
    public async Task use_insert_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        var tracked = await Host.InvokeMessageAndWaitAsync(command);
        
        // Should NOT be trying to send the entity as a cascading message
        tracked.NoRoutes.Envelopes().Any().ShouldBeFalse();

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();

        var todo = persistor.Load<Todo>(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }
    
    [Fact]
    public async Task use_store_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        var tracked = await Host.InvokeMessageAndWaitAsync(command);

        // Should NOT be trying to send the entity as a cascading message
        tracked.NoRoutes.Envelopes().Any().ShouldBeFalse();
        
        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();

        var todo = persistor.Load<Todo>(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }

    [Fact]
    public async Task use_entity_attribute_with_id()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new RenameTodo(command.Id, "New name"));

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();
        var todo = persistor.Load<Todo>(command.Id);
        todo.Name.ShouldBe("New name");
    }
    
    [Fact]
    public async Task use_entity_attribute_with_entity_id()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new RenameTodo2(command.Id, "New name2"));

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();
        var todo = persistor.Load<Todo>(command.Id);
        todo.Name.ShouldBe("New name2");
    }
    
    [Fact]
    public async Task use_entity_attribute_with_explicit_id()
    {
        var command = new CreateTodo(Guid.NewGuid(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new RenameTodo3(command.Id, "New name3"));

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();
        var todo = persistor.Load<Todo>(command.Id);
        todo.Name.ShouldBe("New name3");
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

public record RenameTodo(Guid Id, string Name);
public record RenameTodo2(Guid TodoId, string Name);
public record RenameTodo3(Guid Identity, string Name);

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
    
    // Use "Id" as the default member
    public static Update<Todo> Handle(
        // The first argument is always the incoming message
        RenameTodo command, 
        
        // By using this attribute, we're telling Wolverine
        // to load the Todo entity from the configured
        // persistence of the app using a member on the
        // incoming message type
        [Entity] Todo todo)
    {
        // Do your actual business logic
        todo.Name = command.Name;
        
        // Tell Wolverine that you want this entity
        // updated in persistence
        return Storage.Update(todo);
    }
    
    // Use "TodoId" as the default member
    public static Update<Todo> Handle(RenameTodo2 command, [Entity] Todo todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }
    
    // Use the explicit member
    public static Update<Todo> Handle(RenameTodo3 command, [Entity("Identity")] Todo todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }
}