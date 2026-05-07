using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests;

public class OutboxActivityEvents_emitted : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.ConfigureConventionalLocalRouting()
                    .CustomizeQueues((_, q) => q.UseDurableInbox());
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task emits_flushing_flushed_and_published_events_around_outbox_commit()
    {
        var captured = new List<(string ActivityName, string EventName)>();
        var capturedTags = new List<(string ActivityName, string TagKey, object? TagValue)>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (captured)
                {
                    foreach (var evt in activity.Events)
                    {
                        captured.Add((activity.DisplayName, evt.Name));
                    }
                    foreach (var tag in activity.TagObjects)
                    {
                        capturedTags.Add((activity.DisplayName, tag.Key, tag.Value));
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        await _host.InvokeMessageAndWaitAsync(new ActivityProbeMessage(Guid.NewGuid()));

        var probeEvents = captured
            .Where(c => c.ActivityName.Contains(nameof(ActivityProbeMessage)))
            .Select(c => c.EventName)
            .ToList();

        probeEvents.ShouldContain(WolverineTracing.HandlerUserStarted);
        probeEvents.ShouldContain(WolverineTracing.OutboxFlushing);
        probeEvents.ShouldContain(WolverineTracing.OutboxFlushed);
        probeEvents.ShouldContain(WolverineTracing.OutboxPublished);

        var userStartedIdx = probeEvents.IndexOf(WolverineTracing.HandlerUserStarted);
        var flushingIdx = probeEvents.IndexOf(WolverineTracing.OutboxFlushing);
        var flushedIdx = probeEvents.IndexOf(WolverineTracing.OutboxFlushed);
        var publishedIdx = probeEvents.IndexOf(WolverineTracing.OutboxPublished);
        userStartedIdx.ShouldBeLessThan(flushingIdx);
        flushingIdx.ShouldBeLessThan(flushedIdx);
        flushedIdx.ShouldBeLessThan(publishedIdx);

        capturedTags.ShouldContain(t => t.TagKey == WolverineTracing.EnvelopeTransportLagMs);

        capturedTags.ShouldContain(t =>
            t.ActivityName.Contains(nameof(ActivityProbeMessage)) &&
            t.TagKey == WolverineTracing.EnvelopeAppQueueDwellMs);
    }
}

public record ActivityProbeMessage(Guid Id);

public class ActivityProbeMessageHandler
{
    public void Handle(ActivityProbeMessage message, IDocumentSession session)
    {
        session.Store(new ActivityProbeRecord { Id = message.Id });
    }
}

public class ActivityProbeRecord
{
    public Guid Id { get; set; }
}
