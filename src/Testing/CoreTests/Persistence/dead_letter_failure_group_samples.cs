using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace CoreTests.Persistence;

// Documentation samples for GH-5209
public static class dead_letter_failure_group_samples
{
    public static async Task configure_fingerprinting_and_archive_retention()
    {
        #region sample_configuring_dead_letter_fingerprinting

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Treat your own shared infrastructure as framework code, so the failure group is
                // decided by the first frame from your business code instead
                var fingerprinter = (DefaultDeadLetterFingerprinter)opts.Durability.DeadLetterFingerprinter;
                fingerprinter.FrameworkNamespacePrefixes.Add("MyCompany.Messaging.");

                // Archived dead letters are deleted 30 days after they were archived
                opts.Durability.ArchivedDeadLetterRetention = 30.Days();
            }).StartAsync();

        #endregion
    }

    public static async Task register_a_custom_fingerprinter()
    {
        #region sample_registering_a_custom_dead_letter_fingerprinter

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Replaces the default fingerprinter
                opts.Services.AddSingleton<IDeadLetterFingerprinter, ByMessageTypeAndExceptionFingerprinter>();
            }).StartAsync();

        #endregion
    }

    public static async Task manage_failure_groups(IMessageStore store, CancellationToken token)
    {
        #region sample_managing_dead_letters_as_failure_groups

        var deadLetters = store.DeadLetters;

        // One row per failure group, with the first and last time it happened
        var groups = await deadLetters.SummarizeAllAsync("MyService",
            new DeadLetterSummaryOptions { Grouping = DeadLetterGrouping.FailureGroup }, token);

        foreach (var group in groups)
        {
            Console.WriteLine($"{group.Title}: {group.Count} between {group.FirstFailedAt} and {group.LastFailedAt}");
        }

        var worst = groups.OrderByDescending(x => x.Count).First();

        // After deploying a fix, replay that whole group and report how many messages went back
        var replayed = await deadLetters.ReplayMatchingAsync(
            new DeadLetterEnvelopeQuery { FailureGroup = worst.Key }, token);
        Console.WriteLine($"Replayed {replayed.Affected} message(s)");

        // Or set a group aside without deleting it. Archived dead letters drop out of
        // summaries, queries and replays until they are restored
        if (deadLetters.Capabilities.HasFlag(DeadLetterCapabilities.ArchiveAndRestore))
        {
            await deadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery
            {
                ExceptionMessage = "is not a valid postal code",
                ExceptionMessageMatch = DeadLetterMessageMatch.Contains
            }, token);

            await deadLetters.RestoreMatchingAsync(new DeadLetterEnvelopeQuery
            {
                FailureGroup = worst.Key
            }, token);
        }

        #endregion
    }
}

#region sample_custom_dead_letter_fingerprinter

public class ByMessageTypeAndExceptionFingerprinter : IDeadLetterFingerprinter
{
    public DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception)
    {
        var exceptionType = exception?.GetType().Name ?? "Unknown";
        var title = $"{exceptionType} handling {envelope.MessageType}";

        // Hashed() turns any string into a stable, compact group key
        return DeadLetterFingerprint.Hashed($"{envelope.MessageType}|{exceptionType}", title);
    }
}

#endregion
