using JasperFx.CommandLine;
using Spectre.Console;
using Wolverine.Tracking;

namespace Wolverine.Persistence;

[Description("Clear out all peristed inbox messages marked as `Handled`", Name = "clear-handled")]
public class ClearHandledCommand : JasperFxAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();
        var runtime = host.GetRuntime();
        var stores = await runtime.Stores.FindAllAsync();

        foreach (var store in stores)
        {
            await store.Admin.MigrateAsync();
            
            Console.WriteLine("Starting to clear handled inbox messages in " + store.Uri);

            await store.Admin.DeleteAllHandledAsync();
            Console.WriteLine("Finished clearing handled inbox messages in " + store.Uri);
        }
        
        AnsiConsole.MarkupLine("[green]Finished![/]");

        return true;
    }
}