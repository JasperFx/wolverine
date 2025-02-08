using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using JasperFx;
using JasperFx.CommandLine;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Tracking;

namespace MultiTenantedTodoWebService.Tests;

public class end_to_end : IAsyncLifetime
{
    private IAlbaHost _host;

    private async Task createDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection("Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres");
        await conn.OpenAsync();

        await createDatabaseIfNotExists(conn, "tenant1");
        await createDatabaseIfNotExists(conn, "tenant2");
        await createDatabaseIfNotExists(conn, "tenant3");

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
                opts.Post.Json(new CreateTodo("Kadarious Toney")).ToUrl("/todoitems/tenant1");
                opts.StatusCodeShouldBe(201);
            });

            url = results.Context.Response.Headers.Location!;
        });

        var todo = await _host.GetAsJson<Todo>(url);
        todo!.Name.ShouldBe("Kadarious Toney");
    }

    [Fact]
    public async Task fetch_empty_collection()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/todoitems/tenant2");
        });

        var results = result.ReadAsJson<Todo[]>();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task try_to_load_non_existent_todo()
    {
        await _host.Scenario(x =>
        {
            x.Get.Url("/todoitems/tenant2/2222222");
            x.StatusCodeShouldBe(404);
        });
    }

    #region sample_invoking_by_tenant

    public static async Task invoking_by_tenant(IMessageBus bus)
    {
        // Invoke inline
        await bus.InvokeForTenantAsync("tenant1", new CreateTodo("Release Wolverine 1.0"));

        // Invoke with an expected result (request/response)
        var created =
            await bus.InvokeForTenantAsync<TodoCreated>("tenant2", new CreateTodo("Update the Documentation"));
    }

    #endregion

    #region sample_publish_by_tenant

    public static async Task publish_by_tenant(IMessageBus bus)
    {
        await bus.PublishAsync(new CreateTodo("Fix that last broken test"),
            new DeliveryOptions { TenantId = "tenant3" });
    }

    #endregion
}