using Alba;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
using Shouldly;
using TodoWebService;

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
        var results = await _host.Scenario(opts =>
        {
            opts.Post.Json(new CreateTodo("Kadarious Toney")).ToUrl("/todoitems");
            opts.StatusCodeShouldBe(201);
        });

        var url = results.Context.Response.Headers.Location;

        var todo = await _host.GetAsJson<Todo>(url);
        todo.Name.ShouldBe("Kadarious Toney");
        
    }
}