using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx;
using JasperFx.CommandLine;
using Spectre.Console;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public enum StorageCommandAction
{
    clear,
    counts,
    flush,
    rebuild,
    release,
    replay
}

public class StorageInput : NetCoreInput
{
    [Description("Choose the action")] public StorageCommandAction Action { get; set; } = StorageCommandAction.counts;

    [Description("Optional, specify the file where the schema script would be written")]
    public string FileFlag { get; set; } = "storage.sql";

    [FlagAlias("exception-type", 't')]
    [Description("Optional, specify the exception type that should be replayed. Default is any.")]
    public string ExceptionTypeForReplayFlag { get; set; } = string.Empty;

    [FlagAlias("max-duration", 'd')]
    [Description("Optional, for 'flush': the most seconds the durability pass may run. Default is 30.")]
    public int MaxDurationFlag { get; set; } = 30;

    [FlagAlias("batch-size", 'b')]
    [Description("Optional, for 'flush': the most items each durability pass step handles per batch. Default is DurabilitySettings.RecoveryBatchSize.")]
    public int BatchSizeFlag { get; set; }
}

[Description("Administer the Wolverine message storage")]
public class StorageCommand : JasperFxAsyncCommand<StorageInput>
{
    public StorageCommand()
    {
        Usage("Administer the Wolverine message storage").Arguments(x => x.Action);
    }

    public override async Task<bool> Execute(StorageInput input)
    {
        using var host = input.BuildHost();

        if (input.Action == StorageCommandAction.flush)
        {
            return await flushAsync(host, input);
        }

        var persistence = host.Services.GetRequiredService<IMessageStore>();

        switch (input.Action)
        {
            case StorageCommandAction.counts:

                var counts = await persistence.Admin.FetchCountsAsync();
                Console.WriteLine("Persisted Enveloper Counts");

                var table = new Table();
                table.AddColumns(new TableColumn("Category"), new TableColumn("Count").RightAligned());
                table.AddRow("Incoming", counts.Incoming.ToString());
                table.AddRow("Outgoing", counts.Outgoing.ToString());
                table.AddRow("Scheduled", counts.Scheduled.ToString());
                table.AddRow("Dead Letter", counts.DeadLetter.ToString());
                table.AddRow("Handled", counts.Handled.ToString());

                AnsiConsole.Write(table);

                break;

            case StorageCommandAction.clear:
                await persistence.Admin.ClearAllAsync();
                await host.ClearAllPersistedWolverineDataAsync();
                AnsiConsole.Write("[green]Successfully deleted all persisted envelopes and existing node records[/]");
                break;

            case StorageCommandAction.rebuild:
                await host.RebuildAllEnvelopeStorageAsync();
                AnsiConsole.Write("[green]Successfully rebuilt the envelope storage[/]");
                break;

            case StorageCommandAction.release:
                Console.WriteLine("Releasing all ownership of persisted envelopes");
                await host.ReleaseAllOwnershipAsync();

                break;

            case StorageCommandAction.replay:
                await persistence.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(input.ExceptionTypeForReplayFlag);
                var exceptionType = string.IsNullOrEmpty(input.ExceptionTypeForReplayFlag)
                    ? "any"
                    : input.ExceptionTypeForReplayFlag;
                AnsiConsole.Write(
                    $"[green]Successfully replayed envelope(s) in dead letter with exception type '{exceptionType}'");

                break;
        }

        return true;
    }

    // GH-5016. One bounded durability pass, for a Cloud Run Job, a scheduled Lambda invocation or a
    // Kubernetes CronJob under DurabilityMode.ServerlessWithOutbox. A failed step fails the command so the
    // job reports it
    private static async Task<bool> flushAsync(IHost host, StorageInput input)
    {
        await host.StartAsync();

        try
        {
            var options = new DurabilityPassOptions
            {
                MaxDuration = TimeSpan.FromSeconds(input.MaxDurationFlag > 0 ? input.MaxDurationFlag : 30),
                BatchSize = input.BatchSizeFlag > 0 ? input.BatchSizeFlag : null
            };

            var report = await host.RunDurabilityPassAsync(options);

            var table = new Table();
            table.AddColumns(new TableColumn("Store"), new TableColumn("Lock"), new TableColumn("Step"),
                new TableColumn("Status"), new TableColumn("Processed").RightAligned(),
                new TableColumn("Remaining").RightAligned());

            foreach (var store in report.Stores)
            {
                if (store.Steps.Count == 0)
                {
                    table.AddRow(Markup.Escape(store.StoreUri.ToString()), store.Lock.ToString(), "", "", "", "");
                }

                foreach (var step in store.Steps)
                {
                    table.AddRow(Markup.Escape(store.StoreUri.ToString()), store.Lock.ToString(),
                        Markup.Escape(step.StepName), step.Status.ToString(), step.Result.Processed.ToString(),
                        step.Result.Remaining.ToString());
                }
            }

            AnsiConsole.Write(table);
            Console.WriteLine(report.ToString());

            foreach (var step in report.Stores.SelectMany(x => x.Steps).Where(x => x.Exception != null))
            {
                AnsiConsole.MarkupLine($"[red]Step '{Markup.Escape(step.StepName)}' failed:[/] {Markup.Escape(step.Exception!.Message)}");
            }

            if (report.HasMoreWork)
            {
                AnsiConsole.MarkupLine("[yellow]The durability pass left work behind. Run it again to drain the rest.[/]");
            }

            return report.FailedSteps == 0;
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
