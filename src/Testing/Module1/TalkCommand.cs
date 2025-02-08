using JasperFx;
using JasperFx.CommandLine;
using Spectre.Console;

namespace Module1;

public class TalkCommand : JasperFxCommand<NetCoreInput>
{
    public override bool Execute(NetCoreInput input)
    {
        AnsiConsole.Write("[magenta]Hello![/]");
        return true;
    }
}