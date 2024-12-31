using Alba;
using Marten;
using Shouldly;
using Wolverine.Tracking;
using WolverineWebApi.Todos;

namespace Wolverine.Http.Tests;

public class using_storage_actions_and_entity_attribute : IntegrationContext
{
    public using_storage_actions_and_entity_attribute(AppFixture fixture) : base(fixture)
    {
    }
    
    // These two methods will be changed
    public async Task<Todo2?> Load(string id)
    {
        using var session = Host.DocumentStore().LightweightSession();
        return await session.LoadAsync<Todo2>(id);
    }

    public async Task Persist(Todo2 todo)
    {
        using var session = Host.DocumentStore().LightweightSession();
        session.Store(todo);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task use_insert_as_return_value()
    {
        var command = new CreateTodoRequest(Guid.NewGuid().ToString(), "Write docs");

        await Host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/todo/create");
            x.StatusCodeShouldBe(204);
        });

        var todo = await Load(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }
    
    [Fact]
    public async Task use_store_as_return_value()
    {
        var command = new CreateTodo2(Guid.NewGuid().ToString(), "Write docs");
        await Host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/todo/create2");
            x.StatusCodeShouldBe(204);
        });

        var todo = await Load(command.Id);
        
        todo.Name.ShouldBe("Write docs");
    }

    [Fact]
    public async Task use_entity_attribute_with_id()
    {
        var command = new CreateTodoRequest(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.Scenario(x =>
        {
            x.Post.Json(new RenameTodo(command.Id, "New name")).ToUrl("/api/todo/update");
            x.StatusCodeShouldBe(204);
        });

        var todo = await Load(command.Id);
        todo.Name.ShouldBe("New name");
    }
    
    [Fact]
    public async Task use_entity_attribute_with_entity_id()
    {
        var command = new CreateTodoRequest(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.Scenario(x =>
        {
            x.Post.Json(new RenameTodo2(command.Id, "New name2")).ToUrl("/api/todo/update2");
            x.StatusCodeShouldBe(204);
        });

        var todo = await Load(command.Id);
        todo.Name.ShouldBe("New name2");
    }
    
    [Fact]
    public async Task use_entity_attribute_with_explicit_id()
    {
        var command = new CreateTodoRequest(Guid.NewGuid().ToString(), "Write docs");
        await Host.InvokeMessageAndWaitAsync(command);

        await Host.Scenario(x =>
        {
            x.Post.Json(new RenameTodo3(command.Id, "New name3")).ToUrl("/api/todo/update3");
            x.StatusCodeShouldBe(204);
        });
        
        var todo = await Load(command.Id);
        todo.Name.ShouldBe("New name3");
    }

    [Fact]
    public async Task use_generic_action_as_insert()
    {
        var shouldInsert = new MaybeInsertTodo(Guid.NewGuid().ToString(), "Pick up milk", true);
        var shouldDoNothing = new MaybeInsertTodo(Guid.NewGuid().ToString(), "Start soup", false);

        await Host.Scenario(x =>
        {
            x.Post.Json(shouldInsert).ToUrl("/api/todo/maybeinsert");
            x.StatusCodeShouldBe(204);
        });
        
        await Host.Scenario(x =>
        {
            x.Post.Json(shouldDoNothing).ToUrl("/api/todo/maybeinsert");
            x.StatusCodeShouldBe(204);
        });

        (await Load(shouldInsert.Id)).Name.ShouldBe("Pick up milk");
        (await Load(shouldDoNothing.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task do_nothing_if_storage_action_is_null()
    {
        // Just a smoke test
        var command = new ReturnNullInsert();
        await Host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/todo/nullinsert");
            x.StatusCodeShouldBe(204);
        });
    }
    
    [Fact]
    public async Task do_nothing_if_generic_storage_action_is_null()
    {
        // Just a smoke test
        var command = new ReturnNullStorageAction();
        await Host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/todo/nullaction");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task do_not_execute_the_handler_if_the_entity_is_not_found()
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new CompleteTodo(Guid.NewGuid().ToString())).ToUrl("/api/todo/complete");
            x.StatusCodeShouldBe(404);
        });
        
        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodoRequest(todoId, "Write docs"));
        
        // This should be fine
        await Host.Scenario(x =>
        {
            x.Post.Json(new CompleteTodo(todoId)).ToUrl("/api/todo/complete");
            x.StatusCodeShouldBe(204);
        });
        
        (await Load(todoId)).IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task handler_not_required_entity_attributes()
    {
        // This handler will do nothing if the Todo is null
        await Host.Scenario(x =>
        {
            x.Post.Json(new MaybeCompleteTodo(Guid.NewGuid().ToString())).ToUrl("/api/todo/maybecomplete");
            x.StatusCodeShouldBe(204);
        });

        var todoId = Guid.NewGuid().ToString();
        
        await Host.InvokeMessageAndWaitAsync(new CreateTodoRequest(todoId, "Write docs"));
        
        // This should be fine
        await Host.Scenario(x =>
        {
            x.Post.Json(new MaybeCompleteTodo(todoId)).ToUrl("/api/todo/maybecomplete");
            x.StatusCodeShouldBe(204);
        });
        
        (await Load(todoId)).IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task entity_can_be_used_in_before_methods_implied_from_main_handler_method()
    {
        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodoRequest(todoId, "Write docs"));
        
        // This should be fine

        await Host.Scenario(x =>
        {
            x.Post.Json(new MarkTaskCompleteWithBeforeUsage(todoId)).ToUrl("/api/todo/maybetaskcompletewithbeforeusage");
            x.StatusCodeShouldBe(204);
        });
        
        (await Load(todoId)).IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task can_use_attribute_on_before_methods()
    {
        ExamineFirstHandler.DidContinue = false;
        
        // Negative case, Todo does not exist, main handler should NOT have executed
        await Host.Scenario(x =>
        {
            x.Post.Json(new ExamineFirst(Guid.NewGuid().ToString())).ToUrl("/api/todo/examinefirst");
            x.StatusCodeShouldBe(404);
        });

        ExamineFirstHandler.DidContinue.ShouldBeFalse();
        
        // Positive case, Todo exists
        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodoRequest(todoId, "Write docs"));
        
        await Host.Scenario(x =>
        {
            x.Post.Json(new ExamineFirst(todoId)).ToUrl("/api/todo/examinefirst");
            x.StatusCodeShouldBe(204);
        });

        ExamineFirstHandler.DidContinue.ShouldBeTrue();
        
        
    }

    [Fact]
    public async Task fall_down_to_route_argument_if_no_request_body()
    {
        var todoId = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodoRequest(todoId, "Write docs"));

        // Found
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/api/todo/" + todoId);
        });
        
        result.ReadAsJson<Todo2>().Id.ShouldBe(todoId);
        
        // Miss, should be 404
        await Host.Scenario(x =>
        {
            x.Get.Url("/api/todo/nonexistent");
            x.StatusCodeShouldBe(404);
        });

    }
}