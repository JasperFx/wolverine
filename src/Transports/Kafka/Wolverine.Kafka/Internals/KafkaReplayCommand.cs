using System.Globalization;
using JasperFx;
using JasperFx.CommandLine;
using Spectre.Console;

namespace Wolverine.Kafka.Internals;

public class KafkaReplayInput : NetCoreInput
{
    [Description("The Kafka topic to replay")]
    public string Topic { get; set; } = string.Empty;

    [Description("Start the replay at this absolute offset on every partition")]
    public long? FromOffsetFlag { get; set; }

    [Description("Start at the first record at or after this ISO-8601 timestamp")]
    public string? FromTimestampFlag { get; set; }

    [Description("Stop the replay at this absolute offset (exclusive) on every partition")]
    public long? ToOffsetFlag { get; set; }

    [Description("Stop at the first record at or after this ISO-8601 timestamp")]
    public string? ToTimestampFlag { get; set; }

    [Description("Restrict to these partitions (comma-separated, e.g. 0,1,2)")]
    public string? PartitionsFlag { get; set; }

    [Description("Target a specific named Kafka broker (omit for the default)")]
    public string? BrokerFlag { get; set; }
}

[Description("Replay a window of a Kafka topic's history back through the Wolverine handler pipeline", Name = "kafka-replay")]
public class KafkaReplayCommand : JasperFxAsyncCommand<KafkaReplayInput>
{
    public KafkaReplayCommand()
    {
        Usage("Replay an entire topic from the beginning").Arguments(x => x.Topic);
        Usage("Replay a window of a topic").Arguments(x => x.Topic);
    }

    public override async Task<bool> Execute(KafkaReplayInput input)
    {
        using var host = input.BuildHost();

        var request = new KafkaReplayRequest
        {
            Topic = input.Topic,
            FromOffset = input.FromOffsetFlag,
            FromTimestamp = ParseTimestamp(input.FromTimestampFlag),
            ToOffset = input.ToOffsetFlag,
            ToTimestamp = ParseTimestamp(input.ToTimestampFlag),
            Partitions = input.PartitionsFlag?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToArray()
        };

        AnsiConsole.MarkupLine($"Replaying Kafka topic [blue]{input.Topic}[/]...");
        var result = await host.ReplayKafkaTopicAsync(request, input.BrokerFlag);

        AnsiConsole.MarkupLine(
            $"[green]Replayed {result.RecordsReplayed} record(s) across {result.PartitionsReplayed} partition(s).[/]");

        return true;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        return value == null
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }
}
