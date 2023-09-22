using CoreTests.ErrorHandling;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_559_erroneous_failure_ack : IntegrationContext
{
    public Bug_559_erroneous_failure_ack(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task no_failure_ack()
    {
        var id = Guid.NewGuid();
        var expected = await Publisher.InvokeAsync<Guid>(new Bug559Request(id));
        
        expected.ShouldBe(id);
    }
}

public record Bug559Request(Guid Id);


public static class Bug559RequestHandler
{
    public static Guid Handle(Bug559Request request) => request.Id;
}