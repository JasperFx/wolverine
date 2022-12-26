using Oakton;
using Spectre.Console;

namespace Module1;

public class TalkCommand : OaktonCommand<NetCoreInput>
{
    public override bool Execute(NetCoreInput input)
    {
        AnsiConsole.Write("[magenta]Hello![/]");
        return true;
    }
}