using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_263_returning_string_from_middleware_method
{
    [Fact]
    public async Task can_return_and_use_string_from_tuple()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        await host.InvokeAsync(new Bug263("Tom"));
        
        TupleHandler.Received.ShouldBe("Tom:1");
    }
}

public record Bug263(string Name);

public class TupleHandler
{
    public (string, Context) Load(Bug263 command)
    {
        return ($"{command.Name}:1", new Context());
    }

    public void Handle(Bug263 command, string text, Context context)
    {
        Received = text;
        context.ShouldNotBeNull();
    }

    public static string Received { get; set; }
}

public class Context;