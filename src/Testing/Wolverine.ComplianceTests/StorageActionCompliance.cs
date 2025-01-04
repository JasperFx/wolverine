using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests;

public abstract class StorageActionCompliance : IAsyncLifetime
{
    public List<IDisposable> Disposables = new();
    
    public async Task InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TodoHandler));

                configureWolverine(opts);
            }).StartAsync();
    }

    protected abstract void configureWolverine(WolverineOptions opts);

    public async Task DisposeAsync()
    {
        foreach (var disposable in Disposables)
        {
            disposable.Dispose();
        }
        
        await Host.StopAsync();
    }


    public IHost Host { get; set; }

    // These two methods will be changed
    public abstract Task<Todo?> Load(string id);

    public abstract Task Persist(Todo todo);

    [Fact]
    public async Task use_insert_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        var tracked = await Host.InvokeMessageAndWaitAsync(command);
        
        // Should NOT be trying to send the entity as a cascading message
        tracked.NoRoutes.Envelopes().Any().ShouldBeFalse();

        var todo = await Load(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }
    
    [Fact]
    public async Task use_store_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        var tracked = await Host.InvokeMessageAndWaitAsync(command);

        // Should NOT be trying to send the entity as a cascading message
        tracked.NoRoutes.Envelopes().Any().ShouldBeFalse();

        var todo = await Load(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }

    [Fact]
    public async Task use_entity_attribute_with_id()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new RenameTodo(command.Id, "New name"));

        var todo = await Load(command.Id);
        todo.Name.ShouldBe("New name");
    }
    
    [Fact]
    public async Task use_entity_attribute_with_entity_id()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new RenameTodo2(command.Id, "New name2"));

        var todo = await Load(command.Id);
        todo.Name.ShouldBe("New name2");
    }
    
    [Fact]
    public async Task use_entity_attribute_with_explicit_id()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new RenameTodo3(command.Id, "New name3"));
        
        var todo = await Load(command.Id);
        todo.Name.ShouldBe("New name3");
    }
    
        
    [Fact]
    public async Task use_delete_as_return_value()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        var tracked = await Host.InvokeMessageAndWaitAsync(command);

        // Should NOT be trying to send the entity as a cascading message
        tracked.NoRoutes.Envelopes().Any().ShouldBeFalse();
        
        var tracked2 = await Host.InvokeMessageAndWaitAsync(new DeleteTodo(command.Id));
        tracked2.NoRoutes.Envelopes().Any().ShouldBeFalse();
        
        var todo = await Load(command.Id);
        
        todo.ShouldBeNull();
    }

    [Fact]
    public async Task use_generic_action_as_insert()
    {
        var shouldInsert = new MaybeInsertTodo(Guid.NewGuid().ToString(), "Pick up milk", true);
        var shouldDoNothing = new MaybeInsertTodo(Guid.NewGuid().ToString(), "Start soup", false);

        await Host.InvokeMessageAndWaitAsync(shouldInsert);
        await Host.InvokeMessageAndWaitAsync(shouldDoNothing);

        (await Load(shouldInsert.Id)).Name.ShouldBe("Pick up milk");
        (await Load(shouldDoNothing.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task use_generic_action_as_delete()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new AlterTodo(command.Id, "New text", StorageAction.Delete));

        (await Load(command.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task use_generic_action_as_update()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new AlterTodo(command.Id, "New text", StorageAction.Update));

        (await Load(command.Id)).Name.ShouldBe("New text");
    }
    
    [Fact]
    public async Task use_generic_action_as_store()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new AlterTodo(command.Id, "New text", StorageAction.Store));

        (await Load(command.Id)).Name.ShouldBe("New text");
    }

    [Fact]
    public async Task do_nothing_as_generic_action()
    {
        var command = new CreateTodo(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.InvokeMessageAndWaitAsync(new AlterTodo(command.Id, "New text", StorageAction.Nothing));

        (await Load(command.Id)).Name.ShouldBe("Write docs");
    }

    [Fact]
    public async Task do_nothing_if_storage_action_is_null()
    {
        // Just a smoke test
        var command = new ReturnNullInsert();
        await Host.InvokeMessageAndWaitAsync(command);
    }
    
    [Fact]
    public async Task do_nothing_if_generic_storage_action_is_null()
    {
        // Just a smoke test
        var command = new ReturnNullStorageAction();
        await Host.InvokeMessageAndWaitAsync(command);
    }
}


public class Todo
{
    public string Id { get; set; }
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}

public record CreateTodo(string Id, string Name);
public record CreateTodo2(string Id, string Name);

public record DeleteTodo(string Id);

public record RenameTodo(string Id, string Name);
public record RenameTodo2(string TodoId, string Name);
public record RenameTodo3(string Identity, string Name);

public record AlterTodo(string Id, string Name, StorageAction Action);

public record MaybeInsertTodo(string Id, string Name, bool ShouldInsert);

public record ReturnNullInsert;
public record ReturnNullStorageAction;

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

    public static Delete<Todo> Handle(DeleteTodo command, [Entity("Identity")] Todo todo)
    {
        return Storage.Delete(todo);
    }
    
    public static IStorageAction<Todo> Handle(AlterTodo command, [Entity("Identity")] Todo todo)
    {
        switch (command.Action)
        {
            case StorageAction.Delete:
                return Storage.Delete(todo);
            case StorageAction.Update:
                todo.Name = command.Name;
                return Storage.Update(todo);
            case StorageAction.Store:
                todo.Name = command.Name;
                return Storage.Store(todo);
            default:
                return Storage.Nothing<Todo>();
        }
    }

    public static IStorageAction<Todo> Handle(MaybeInsertTodo command)
    {
        if (command.ShouldInsert)
        {
            return Storage.Insert(new Todo { Id = command.Id, Name = command.Name });
        }

        return Storage.Nothing<Todo>();
    }

    public static Insert<Todo>? Handle(ReturnNullInsert command) => null;

    public static IStorageAction<Todo>? Handle(ReturnNullStorageAction command) => null;
}