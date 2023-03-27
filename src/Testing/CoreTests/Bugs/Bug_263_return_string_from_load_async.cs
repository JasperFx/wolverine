using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_263_return_string_from_load_async
{
    [Fact]
    public async Task can_return_a_string_from_a_load_precursor_and_pass_to_main_handle()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        await host.InvokeMessageAndWaitAsync(new StringUsingCommand("one"));
        
        StringUsingCommandHandler.Recorded.ShouldBe("one:FromLoad");
    }
}

public record StringUsingCommand(string Name);

public class StringUsingCommandHandler
{
    public string Load(StringUsingCommand command)
    {
        return "FromLoad";
    }

    public void Handle(StringUsingCommand command, string text)
    {
        Recorded = $"{command.Name}:{text}";
    }

    public static string Recorded { get; set; }
}