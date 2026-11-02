using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
/// GH-5312. A scheduled retry on a durable endpoint lost the GH-4398 link to the attempt that failed: the
/// previous attempt's activity id was put in a header on the in-memory envelope, but rescheduling left the stored
/// body -- the serialized envelope, headers and all -- as it was on arrival, so the header was gone by the time the
/// scheduled row came due and was read back.
/// </summary>
public class Bug_5312_durable_scheduled_retry_keeps_the_attempt_link : IAsyncLifetime
{
    private const string QueueName = "gh5312-durable";

    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "gh5312");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5312SqlServerHandler));

                opts.OnException<Gh5312SqlServerFailure>().ScheduleRetry(150.Milliseconds());

                opts.PublishMessage<Gh5312SqlServerMessage>().ToLocalQueue(QueueName);
                opts.LocalQueue(QueueName).UseDurableInbox();
            }).StartAsync();

        await _host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public async Task the_scheduled_attempt_links_to_the_failed_attempt()
    {
        var id = Guid.NewGuid();

        await _host.MessageBus().PublishAsync(new Gh5312SqlServerMessage(id));

        var spans = await Gh5312SqlServerHandler.WaitForAttemptsAsync(id, 2);
        spans.Length.ShouldBe(2);

        var attempts = await waitForStoppedAsync(spans);

        attempts[0].Links.ShouldBeEmpty();

        var link = attempts[1].Links.ShouldHaveSingleItem();
        link.Context.TraceId.ShouldBe(attempts[0].TraceId);
        link.Context.SpanId.ShouldBe(attempts[0].SpanId);
    }

    private async Task<Activity[]> waitForStoppedAsync(ActivitySpanId[] spans)
    {
        // The handler records the span it runs in before that span stops
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var stopped = _stopped.ToArray();
            var matches = spans.Select(s => stopped.FirstOrDefault(a => a.SpanId == s)).ToArray();
            if (matches.All(x => x != null)) return matches!;

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The attempt spans never stopped");
            }

            await Task.Delay(25);
        }
    }
}

public record Gh5312SqlServerMessage(Guid Id);

public class Gh5312SqlServerFailure() : Exception("Failing so the message is rescheduled");

public static class Gh5312SqlServerHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<ActivitySpanId>> _spans = new();

    public static void Handle(Gh5312SqlServerMessage message)
    {
        var spans = _spans.GetOrAdd(message.Id, _ => new ConcurrentQueue<ActivitySpanId>());
        spans.Enqueue(Activity.Current!.SpanId);

        if (spans.Count == 1) throw new Gh5312SqlServerFailure();
    }

    public static async Task<ActivitySpanId[]> WaitForAttemptsAsync(Guid id, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_spans.TryGetValue(id, out var spans) && spans.Count >= count) return spans.ToArray();
            await Task.Delay(50);
        }

        return _spans.TryGetValue(id, out var found) ? found.ToArray() : [];
    }
}
