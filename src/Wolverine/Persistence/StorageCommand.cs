using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
using Spectre.Console;
using Wolverine.Persistence.Durability;

namespace Wolverine.Persistence;

public enum StorageAction
{
    clear,
    counts,
    rebuild,
    release,
    replay
}

public class StorageInput : NetCoreInput
{
    [Description("Choose the action")] public StorageAction Action { get; set; } = StorageAction.counts;

    [Description("Optional, specify the file where the schema script would be written")]
    public string FileFlag { get; set; } = "storage.sql";

    [FlagAlias("exception-type", 't')]
    [Description("Optional, specify the exception type that should be replayed. Default is any.")]
    public string ExceptionTypeForReplayFlag { get; set; } = string.Empty;
}

[Description("Administer the envelope storage")]
public class StorageCommand : OaktonAsyncCommand<StorageInput>
{
    public StorageCommand()
    {
        Usage("Administer the envelope storage").Arguments(x => x.Action);
    }

    public override async Task<bool> Execute(StorageInput input)
    {
        using var host = input.BuildHost();
        var persistence = host.Services.GetRequiredService<IMessageStore>();

        persistence.Describe(Console.Out);

        switch (input.Action)
        {
            case StorageAction.counts:

                var counts = await persistence.Admin.FetchCountsAsync();
                Console.WriteLine("Persisted Enveloper Counts");

                var table = new Table();
                table.AddColumns(new TableColumn("Category"), new TableColumn("Count").RightAligned());
                table.AddRow("Incoming", counts.Incoming.ToString());
                table.AddRow("Outgoing", counts.Outgoing.ToString());
                table.AddRow("Scheduled", counts.Scheduled.ToString());
                table.AddRow("Dead Letter", counts.DeadLetter.ToString());
                
                AnsiConsole.Write(table);

                break;

            case StorageAction.clear:
                await persistence.Admin.ClearAllAsync();
                AnsiConsole.Write("[green]Successfully deleted all persisted envelopes[/]");
                break;

            case StorageAction.rebuild:
                await persistence.Admin.RebuildAsync();
                AnsiConsole.Write("[green]Successfully rebuilt the envelope storage[/]");
                break;

            case StorageAction.release:
                await persistence.Admin.RebuildAsync();
                Console.WriteLine("Releasing all ownership of persisted envelopes");
                await persistence.Admin.ReleaseAllOwnershipAsync();

                break;
            
            case StorageAction.replay:
                var markedCount = await persistence.Admin.MarkDeadLetterEnvelopesAsReplayableAsync(input.ExceptionTypeForReplayFlag);
                var exceptionType = string.IsNullOrEmpty(input.ExceptionTypeForReplayFlag)
                    ? "any" 
                    : input.ExceptionTypeForReplayFlag;
                AnsiConsole.Write($"[green]Successfully replayed {markedCount} envelope(s) in dead letter with exception type '{exceptionType}'");

                break;
        }

        return true;
    }
}