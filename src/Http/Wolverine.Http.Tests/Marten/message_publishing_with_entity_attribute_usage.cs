using Marten;
using Shouldly;
using WolverineWebApi.Marten;
using WolverineWebApi.Todos;

namespace Wolverine.Http.Tests.Marten;

public class message_publishing_with_entity_attribute_usage : IntegrationContext
{
    public message_publishing_with_entity_attribute_usage(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task call_with_completed_todo()
    {
        var todo = new Todo2 { Id = Guid.NewGuid().ToString(), IsComplete = true};
        using var session = Host.DocumentStore().LightweightSession();
        session.Store(todo);
        await session.SaveChangesAsync();

        var (tracked, response) = await TrackedHttpCall(x =>
        {
            x.Post.Url($"/api/todo2/{todo.Id}/something");
            x.StatusCodeShouldBe(202);
        });
        
        tracked.Received.SingleMessage<DoSomethingWithTodo>()
            .TodoId.ShouldBe(todo.Id);
    }
}