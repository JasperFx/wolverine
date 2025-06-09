using Alba;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using JasperFx;
using JasperFx.CommandLine;
using Shouldly;
using TodoWebService;
using Wolverine.Tracking;

namespace TodoWebServiceTests;

public class end_to_end : IAsyncLifetime
{
    private IAlbaHost _host;

    public async Task InitializeAsync()
    {
        // Sorry folks, this is a hidden trap
        // I blame the AspNetCore team...
        JasperFxEnvironment.AutoStartHost = true;

        _host = await AlbaHost.For<Program>();

        // Wiping out any leftover data in the database
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public Task DisposeAsync()
    {
        return _host.DisposeAsync().AsTask();
    }

    #region sample_testing_hello_world_for_http

    [Fact]
    public async Task hello_world()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        result.ReadAsText().ShouldBe("Hello.");
    }

    #endregion

    [Fact]
    public async Task create_and_load()
    {
        string url = null!;

        // I'm making Wolverine "wait" for all message activity that's started
        // within the supplied action to finish
        var tracked = await _host.ExecuteAndWaitAsync(async _ =>
        {
            var results = await _host.Scenario(opts =>
            {
                opts.Post.Json(new CreateTodo("Kadarious Toney")).ToUrl("/todoitems");
                opts.StatusCodeShouldBe(201);
            });

            url = results.Context.Response.Headers.Location!;
        });

        var todo = await _host.GetAsJson<Todo>(url);
        todo!.Name.ShouldBe("Kadarious Toney");

        // Now let's see if the message got kicked out
        var @event = tracked.Executed.SingleMessage<TodoCreated>();
        @event.Id.ShouldBe(todo.Id);
    }

    [Fact]
    public async Task fetch_empty_collection()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/todoitems");
        });

        var results = result.ReadAsJson<Todo[]>();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task try_to_load_non_existent_todo()
    {
        await _host.Scenario(x =>
        {
            x.Get.Url("/todoitems/2222222");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task update_when_the_todo_does_not_exist()
    {
        await _host.Scenario(x =>
        {
            // This Todo does not exist because we wiped out the database in the test
            // setup
            x.Put.Json(new UpdateTodo(1, "Skyy Moore", false)).ToUrl("/todoitems");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task use_iresult_as_main_response_body()
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoListRequest("Help me Obi Wan Kenobi!")).ToUrl("/api/todo-lists/");
            x.StatusCodeShouldBe(201);

        });


    }

    [Fact]
    public async Task post_invalid_json()
    {
        var results = await _host.Scenario(opts =>
        {
            dynamic wrongJson = new { Title = true, PropertyTwo = false };
            opts.Post.Json(wrongJson).ToUrl("/api/todo-lists");
            opts.StatusCodeShouldBe(400);
        });
        var problemDetails = results.ReadAsJson<ProblemDetails>();
        problemDetails.Detail.ShouldBe("The JSON value could not be converted to TodoWebService.CreateTodoListRequest. Path: $.title | LineNumber: 0 | BytePositionInLine: 13.");
        problemDetails.Status.ShouldBe(400);
        problemDetails.Title.ShouldBe("Invalid JSON format");
        problemDetails.Type.ShouldBe("https://httpstatuses.com/400");
    }
}