using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests;

public class envelope_id_generation : IDisposable
{
    public void Dispose()
    {
        // Reset to default after each test
        Envelope.IdGenerator = NewId.NextSequentialGuid;
    }

    [Fact]
    public void default_envelope_id_generation_is_new_id()
    {
        var options = new WolverineOptions();
        options.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
    }

    [Fact]
    public void new_id_mode_produces_non_empty_guids()
    {
        Envelope.IdGenerator = NewId.NextSequentialGuid;

        var envelope = new Envelope();
        envelope.Id.ShouldNotBe(Guid.Empty);
    }

#if NET9_0_OR_GREATER
    [Fact]
    public void guid_v7_mode_produces_version_7_guids()
    {
        Envelope.IdGenerator = Guid.CreateVersion7;

        var envelope = new Envelope();
        envelope.Id.ShouldNotBe(Guid.Empty);

        // Version 7 GUIDs have version bits indicating V7
        ((int)envelope.Id.Version).ShouldBe(7);
    }

    [Fact]
    public void guid_v7_ids_are_roughly_time_ordered()
    {
        Envelope.IdGenerator = Guid.CreateVersion7;

        // Generate IDs with a small delay between batches to ensure different timestamps
        var batch1 = Enumerable.Range(0, 10).Select(_ => new Envelope().Id).ToList();
        Thread.Sleep(10);
        var batch2 = Enumerable.Range(0, 10).Select(_ => new Envelope().Id).ToList();

        // The max from batch1 should be less than the min from batch2
        // because they were generated at different timestamps
        batch1.Max().ShouldBeLessThan(batch2.Min());
    }

    [Fact]
    public void guid_v7_ids_are_unique_across_threads()
    {
        Envelope.IdGenerator = Guid.CreateVersion7;

        var ids = new ConcurrentBag<Guid>();
        var tasks = new List<Task>();

        // Simulate the scenario from the bug report: multiple threads generating IDs
        for (var t = 0; t < 10; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    ids.Add(new Envelope().Id);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        ids.Count.ShouldBe(10_000);
        ids.Distinct().Count().ShouldBe(10_000, "All IDs should be unique");
    }

    [Fact]
    public async Task invoke_async_works_with_guid_v7()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.EnvelopeIdGeneration = EnvelopeIdGeneration.GuidV7;
            }).StartAsync();

        var tracked = await host.InvokeMessageAndWaitAsync(new GuidV7TestMessage("hello"));

        GuidV7TestHandler.LastReceived.ShouldBe("hello");
    }
#endif

    [Fact]
    public void all_newid_usages_respect_id_generator()
    {
        // Verify that setting the delegate affects all envelope creation
        var customId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        Envelope.IdGenerator = () => customId;

        var envelope = new Envelope();
        envelope.Id.ShouldBe(customId);

        var envelope2 = new Envelope(new GuidV7TestMessage("test"));
        envelope2.Id.ShouldBe(customId);
    }
}

public record GuidV7TestMessage(string Value);

public static class GuidV7TestHandler
{
    public static string? LastReceived;

    public static void Handle(GuidV7TestMessage message)
    {
        LastReceived = message.Value;
    }
}
