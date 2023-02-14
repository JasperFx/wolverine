using Alba;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
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
        OaktonEnvironment.AutoStartHost = true;
        
        _host = await AlbaHost.For<Program>();

        // Wiping out any leftover data in the database
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public Task DisposeAsync()
    {
        return _host.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task create_and_load()
    {
        string url = default;
        
        // I'm making Wolverine "wait" for all message activity that's started
        // within the supplied action to finish
        var tracked = await _host.ExecuteAndWaitAsync(async _ =>
        {
            var results = await _host.Scenario(opts =>
            {
                opts.Post.Json(new CreateTodo("Kadarious Toney")).ToUrl("/todoitems");
                opts.StatusCodeShouldBe(201);
            });

            url = results.Context.Response.Headers.Location;
        });
        
        var todo = await _host.GetAsJson<Todo>(url);
        todo.Name.ShouldBe("Kadarious Toney");

        // Now let's see if the message got kicked out
        var @event = tracked.Executed.SingleMessage<TodoCreated>();
        @event.Id.ShouldBe(todo.Id);
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
}