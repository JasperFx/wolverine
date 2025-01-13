using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
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
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TodoHandler))
                    .IncludeType(typeof(MarkTaskCompleteIfBrokenHandler))
                    .IncludeType(typeof(ExamineFirstHandler))
                    .IncludeType(typeof(StoreManyHandler));

                configureWolverine(opts);
            }).StartAsync();

        await initialize();
    }

    protected virtual Task initialize()
    {
        return Task.CompletedTask;
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

    [Fact]
    public async Task do_not_execute_the_handler_if_the_entity_is_not_found()
    {
        // This handler would blow up if the Todo is null
        await Host.InvokeMessageAndWaitAsync(new CompleteTodo(Guid.NewGuid().ToString()));

        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodo(todoId, "Write docs"));
        
        // This should be fine
        await Host.InvokeMessageAndWaitAsync(new CompleteTodo(todoId));
        
        (await Load(todoId)).IsComplete.ShouldBeTrue();
    }
    
    [Fact]
    public async Task handler_not_required_entity_attributes()
    {
        // This handler will do nothing if the Todo is null
        await Host.InvokeMessageAndWaitAsync(new MaybeCompleteTodo(Guid.NewGuid().ToString()));

        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodo(todoId, "Write docs"));
        
        // This should be fine
        await Host.InvokeMessageAndWaitAsync(new MaybeCompleteTodo(todoId));
        
        (await Load(todoId)).IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task entity_can_be_used_in_before_methods_implied_from_main_handler_method()
    {
        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodo(todoId, "Write docs"));
        
        // This should be fine
        await Host.InvokeMessageAndWaitAsync(new MarkTaskCompleteWithBeforeUsage(todoId));
        
        (await Load(todoId)).IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task can_use_attribute_on_before_methods()
    {
        ExamineFirstHandler.DidContinue = false;
        
        // Negative case, Todo does not exist, main handler should NOT have executed
        await Host.InvokeMessageAndWaitAsync(new ExamineFirst(Guid.NewGuid().ToString()));
        ExamineFirstHandler.DidContinue.ShouldBeFalse();
        
        // Positive case, Todo exists
        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodo(todoId, "Write docs"));
        await Host.InvokeMessageAndWaitAsync(new ExamineFirst(todoId));

        ExamineFirstHandler.DidContinue.ShouldBeTrue();
        
        
    }

    [Fact]
    public async Task use_unit_of_work_as_return_value()
    {
        var storeMany = new StoreMany([Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]);
        await Host.InvokeMessageAndWaitAsync(storeMany);

        (await Load(storeMany.Adds[0])).ShouldNotBeNull();
        (await Load(storeMany.Adds[1])).ShouldNotBeNull();
        (await Load(storeMany.Adds[2])).ShouldNotBeNull();

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

    public static IStorageAction<Todo> Handle(CompleteTodo command, [Entity] Todo todo)
    {
        if (todo == null) throw new ArgumentNullException(nameof(todo));
        todo.IsComplete = true;
        return Storage.Update(todo);
    }
    
    public static IStorageAction<Todo> Handle(MaybeCompleteTodo command, [Entity(Required = false)] Todo? todo)
    {
        if (todo == null) return Storage.Nothing<Todo>();
        todo.IsComplete = true;
        return Storage.Update(todo);
    }
}

public record CompleteTodo(string Id);
public record MaybeCompleteTodo(string Id);

public record MarkTaskCompleteWithBeforeUsage(string Id);

public static class MarkTaskCompleteIfBrokenHandler
{
    // Just proving out that you can get at the entity
    // in a Before method
    public static void Before(Todo todo)
    {
        todo.ShouldNotBeNull();
    }
    
    public static Update<Todo> Handle(MarkTaskCompleteWithBeforeUsage command, [Entity] Todo todo)
    {
        todo.IsComplete = true;
        return Storage.Update(todo);
    }
}

public record ExamineFirst(string TodoId);

public static class ExamineFirstHandler
{
    public static bool DidContinue { get; set; }
    
    public static HandlerContinuation Before([Entity] Todo todo)
    {
        return todo.IsComplete ? HandlerContinuation.Stop : HandlerContinuation.Continue;
    }

    public static void Handle(ExamineFirst command) => DidContinue = true;
}

#region sample_using_unit_of_work_as_side_effect

public record StoreMany(string[] Adds);

public static class StoreManyHandler
{
    public static UnitOfWork<Todo> Handle(StoreMany command)
    {
        var uow = new UnitOfWork<Todo>();
        foreach (var add in command.Adds)
        {
            uow.Insert(new Todo { Id = add });
        }

        return uow;
    }
}

#endregion

