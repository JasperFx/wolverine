using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Serilog;

// One Wolverine.AzureServiceBus.Tests run at a time on this box, whichever worktree or target starts it.
//
// The suite deletes every queue and topic in its emulator, from many places and between tests, and the emulator caps
// a namespace at 50 entities. Two runs against the same emulator delete each other's entities mid-test and run into
// the cap: on 2026-11-17 two LocalGate runs in different worktrees did exactly that, took 80 and 83 minutes instead of
// 31, and each still ended with 8 to 27 failures after using up its retries. The asb-emulator track only keeps units
// apart inside one gate process, and the fresh-container check only declines to recreate an emulator in use; it does
// not wait. This lock waits.
//
// Held from just before the emulators are (re)started until the run is over. A file lock, so the OS drops it when the
// process dies and a killed build never leaves it held.
partial class Build
{
    /// <summary>How long a run waits for another run's Azure Service Bus tests before it gives up.</summary>
    static readonly TimeSpan AzureServiceBusLockBudget = TimeSpan.FromHours(3);

    /// <summary>
    /// Waits until no other run on this box holds the Azure Service Bus suite lock, then holds it until disposed.
    /// </summary>
    static IDisposable acquireAzureServiceBusLock()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wolverine", "locks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "asb-suite.lock");
        var ownerPath = Path.Combine(directory, "asb-suite.owner");

        var clock = Stopwatch.StartNew();
        var lastLog = TimeSpan.MinValue;
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive flock on Unix and a share-mode lock on Windows
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                writeOwner(ownerPath);
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                {
                    Log.Information("Got the Azure Service Bus suite lock after waiting {Waited}", humanize(clock.Elapsed));
                }

                return stream;
            }
            catch (IOException) when (clock.Elapsed < AzureServiceBusLockBudget)
            {
                if (lastLog == TimeSpan.MinValue || clock.Elapsed - lastLog > TimeSpan.FromMinutes(5))
                {
                    Log.Information(
                        "Another run on this box is running Wolverine.AzureServiceBus.Tests ({Owner}); waiting for it to finish ({Path}, waited {Waited})",
                        readOwner(ownerPath), path, humanize(clock.Elapsed));
                    lastLog = clock.Elapsed;
                }

                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
            catch (IOException e)
            {
                throw new InvalidOperationException(
                    $"Waited {humanize(AzureServiceBusLockBudget)} for another run's Azure Service Bus tests ({readOwner(ownerPath)}) " +
                    $"to release {path}. Two runs on one emulator delete each other's queues, so this one does not start.", e);
            }
        }
    }

    static void writeOwner(string ownerPath)
    {
        try
        {
            File.WriteAllText(ownerPath,
                $"pid {Environment.ProcessId} in {RootDirectory}, since {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        }
        catch (IOException)
        {
            // Only ever informational
        }
    }

    static string readOwner(string ownerPath)
    {
        try
        {
            return File.Exists(ownerPath) ? File.ReadAllText(ownerPath).Trim() : "owner unknown";
        }
        catch (IOException)
        {
            return "owner unknown";
        }
    }
}
