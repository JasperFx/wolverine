using Marten.Schema;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence;
using WolverineWebApi.Samples;

namespace WolverineWebApi.Todos;

[DocumentAlias("test_todo")]
public class Todo2
{
    public string Id { get; set; }
    public string? Name { get; set; }
    public bool IsComplete { get; set; }
}

public record CreateTodoRequest(string Id, string Name);
public record CreateTodo2(string Id, string Name);

public record DeleteTodo(string Id);

#region sample_rename_todo

public record RenameTodo(string Id, string Name);

#endregion
public record RenameTodo2(string Todo2Id, string Name);
public record RenameTodo3(string Identity, string Name);

public record AlterTodo(string Id, string Name, StorageAction Action);

public record MaybeInsertTodo(string Id, string Name, bool ShouldInsert);

public record ReturnNullInsert;
public record ReturnNullStorageAction;


public static class TodoHandler
{
    [WolverinePost("/api/todo/create")]
    public static Insert<Todo2> Handle(CreateTodoRequest command) => Storage.Insert(new Todo2
    {
        Id = command.Id,
        Name = command.Name
    });
    
    [WolverinePost("/api/todo/create2")]
    public static Store<Todo2> Handle(CreateTodo2 command) => Storage.Store(new Todo2
    {
        Id = command.Id,
        Name = command.Name
    });

    #region sample_using_entity_attribute

    // Use "Id" as the default member
    [WolverinePost("/api/todo/update")]
    public static Update<Todo2> Handle(
        // The first argument is always the incoming message
        RenameTodo command, 
        
        // By using this attribute, we're telling Wolverine
        // to load the Todo entity from the configured
        // persistence of the app using a member on the
        // incoming message type
        [Entity] Todo2 todo)
    {
        // Do your actual business logic
        todo.Name = command.Name;
        
        // Tell Wolverine that you want this entity
        // updated in persistence
        return Storage.Update(todo);
    }

    #endregion
    
    // Use "TodoId" as the default member
    [WolverinePost("/api/todo/update2")]
    public static Update<Todo2> Handle(RenameTodo2 command, [Entity] Todo2 todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }
    
    // Use the explicit member
    [WolverinePost("/api/todo/update3")]
    public static Update<Todo2> Handle(RenameTodo3 command, [Entity("Identity")] Todo2 todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }

    [WolverineDelete("/api/todo/delete")]
    public static Delete<Todo2> Handle(DeleteTodo command, [Entity("Identity")] Todo2 todo)
    {
        return Storage.Delete(todo);
    }
    
    [WolverinePost("/api/todo/alter")]
    public static IStorageAction<Todo2> Handle(AlterTodo command, [Entity("Identity")] Todo2 todo)
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
                return Storage.Nothing<Todo2>();
        }
    }

    [WolverinePost("/api/todo/maybeinsert")]
    public static IStorageAction<Todo2> Handle(MaybeInsertTodo command)
    {
        if (command.ShouldInsert)
        {
            return Storage.Insert(new Todo2 { Id = command.Id, Name = command.Name });
        }

        return Storage.Nothing<Todo2>();
    }

    [WolverinePost("/api/todo/nullinsert")]
    public static Insert<Todo2>? Handle(ReturnNullInsert command) => null;

    [WolverinePost("/api/todo/nullaction")]
    public static IStorageAction<Todo2>? Handle(ReturnNullStorageAction command) => null;

    [WolverinePost("/api/todo/complete")]
    public static IStorageAction<Todo2> Handle(CompleteTodo command, [Entity] Todo2 todo)
    {
        if (todo == null) throw new ArgumentNullException(nameof(todo));
        todo.IsComplete = true;
        return Storage.Update(todo);
    }

    #region sample_using_not_required_entity_attribute

    [WolverinePost("/api/todo/maybecomplete")]
    public static IStorageAction<Todo2> Handle(MaybeCompleteTodo command, [Entity(Required = false)] Todo2? todo)
    {
        if (todo == null) return Storage.Nothing<Todo2>();
        todo.IsComplete = true;
        return Storage.Update(todo);
    }

    #endregion

    #region sample_specifying_the_exact_route_argument

    // Okay, I still used "id", but it *could* be something different here!
    [WolverineGet("/api/todo/{id}")]
    public static Todo2 Get([Entity("id")] Todo2 todo) => todo;

    #endregion
}

public record CompleteTodo(string Id);
public record MaybeCompleteTodo(string Id);

public record MarkTaskCompleteWithBeforeUsage(string Id);

public static class MarkTaskCompleteIfBrokenHandler
{
    // Just proving out that you can get at the entity
    // in a Before method
    public static void Before(Todo2 todo)
    {
        todo.ShouldNotBeNull();
    }
    
    [WolverinePost("/api/todo/maybetaskcompletewithbeforeusage")]
    public static Update<Todo2> Handle(MarkTaskCompleteWithBeforeUsage command, [Entity] Todo2 todo)
    {
        todo.IsComplete = true;
        return Storage.Update(todo);
    }
}

public record ExamineFirst(string Todo2Id);

public static class ExamineFirstHandler
{
    public static bool DidContinue { get; set; }
    
    public static IResult Before([Entity] Todo2 todo)
    {
        return todo != null ? WolverineContinue.Result() : Results.Empty;
    }

    [WolverinePost("/api/todo/examinefirst")]
    public static void Handle(ExamineFirst command) => DidContinue = true;
}

