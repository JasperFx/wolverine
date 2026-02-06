using System.Diagnostics;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_2004_separated_handler_stuff
{
    [Fact]
    public async Task multiple_handler_file_overwrite()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            })
            .StartAsync();

        await host.SendMessageAndWaitAsync(new SayStuff0());
    }
}

public record SayStuff0();

public record SayStuff1(string Text);
public record SayStuff2(string Text);

public class BSayStuffHandler
{

    public (SayStuff1, SayStuff2) Handle(SayStuff0 _)
    {
        return (new SayStuff1("Hello"), new SayStuff2("World"));
    }
    public void Handle(SayStuff1 stuff) => Debug.WriteLine(stuff.Text);
    public void Handle(SayStuff2 stuff) => Debug.WriteLine(stuff.Text);
}

public class ASayStuffHandler
{
    public void Handle(SayStuff1 stuff) => Debug.WriteLine(stuff.Text);
    public void Handle(SayStuff2 stuff) => Debug.WriteLine(stuff.Text);
}