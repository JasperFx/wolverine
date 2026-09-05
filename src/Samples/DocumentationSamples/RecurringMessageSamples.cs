using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class RecurringMessageSamples
{
    public static async Task configure()
    {
        #region sample_registering_recurring_messages

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // Simplest possible usage: a message type with a public, no-argument
            // constructor, published on a cron schedule. The schedule's name defaults
            // to the message type's name
            opts.Schedules.RecurringMessage<RunNightlyRollup>("0 2 * * *");

            // Or build the message per occurrence — the factory is handed the
            // occurrence time, so a message can describe the window it covers
            opts.Schedules.RecurringMessage(
                "daily-report",
                "0 9 * * *",
                occurrence => new BuildDailyReport(occurrence.AddDays(-1), occurrence));

            // Cron expressions parse into a first class value type that you can
            // construct, hold, and reuse — including with an explicit time zone
            var nineAmCentral = new CronSchedule(
                "0 9 * * *",
                TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));

            opts.Schedules.RecurringMessage<SendMorningDigest>(nineAmCentral);
        });

        #endregion
    }
}

#region sample_recurring_message_types

// Recurring messages are handled like any other message — the cron machinery
// only decides WHEN they are published, never how they are processed
public record RunNightlyRollup;

public record BuildDailyReport(DateTimeOffset From, DateTimeOffset To);

public record SendMorningDigest;

public static class RecurringSampleHandler
{
    public static void Handle(RunNightlyRollup message)
        => Console.WriteLine("Rolling up!");

    public static void Handle(BuildDailyReport message)
        => Console.WriteLine($"Reporting on {message.From} to {message.To}");

    public static void Handle(SendMorningDigest message)
        => Console.WriteLine("Good morning!");
}

#endregion
