using Shouldly;
using WolverineWebApi.Samples;

namespace Wolverine.Http.Tests;

/// <summary>
/// These specs were mostly built to support the HTTP enhancements in Wolverine 1.2
/// </summary>
public class todo_endpoint_specs : IntegrationContext
{
    public todo_endpoint_specs(AppFixture fixture) : base(fixture)
    {
    }

    [Theory]
    [InlineData("/todos/")]
    [InlineData("/todos/validation/")]
    [InlineData("/todos/middleware/")]
    [InlineData("/todos/policy/")]
    public async Task wolverine_can_handle_route_constraints(string baseUrl)
    {
        await using var session = Store.LightweightSession();
        var todo = new Todo { Name = "First", IsComplete = false };
        session.Store(todo);
        await session.SaveChangesAsync();

        await Scenario(opts =>
        {
            opts.Put.Json(new UpdateRequest("Second", true)).ToUrl(baseUrl + todo.Id);
            opts.StatusCodeShouldBe(204);
        });

        var changes = await session.LoadAsync<Todo>(todo.Id);
        changes.IsComplete.ShouldBeTrue();
        changes.Name.ShouldBe("Second");
    }

    [Fact]
    public async Task bug_466_codegen_error()
    {
        await using var session = Store.LightweightSession();
        var todo = new Todo { Name = "First", IsComplete = false };
        session.Store(todo);
        await session.SaveChangesAsync();

        await Scenario(opts =>
        {
            opts.Put.Json(new UpdateRequest("Second", true)).ToUrl("/todos2/" + todo.Id);
        });

        var changes = await session.LoadAsync<Todo>(todo.Id);
        changes.IsComplete.ShouldBeTrue();
        changes.Name.ShouldBe("Second");
    }

    [Fact]
    public async Task get_an_automatic_404_when_related_entity_does_not_exist()
    {
        await Scenario(opts =>
        {
            opts.Put.Json(new UpdateRequest("Second", true)).ToUrl("/todos/1222222222");
            opts.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task get_an_automatic_404_when_related_entity_does_not_exist_with_validation()
    {
        await Scenario(opts =>
        {
            opts.Put.Json(new UpdateRequest("Second", true)).ToUrl("/todos/validation/1222222222");
            opts.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task get_an_automatic_404_when_related_entity_does_not_exist_with_middleware()
    {
        await Scenario(opts =>
        {
            opts.Put.Json(new UpdateRequest("Second", true)).ToUrl("/todos/middleware/1222222222");
            opts.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task get_an_automatic_404_when_related_entity_does_not_exist_with_policy()
    {
        await Scenario(opts =>
        {
            opts.Put.Json(new UpdateRequest("Second", true)).ToUrl("/todos/policy/1222222222");
            opts.StatusCodeShouldBe(404);
        });
    }
}