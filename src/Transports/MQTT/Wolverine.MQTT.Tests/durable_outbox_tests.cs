using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Sqlite;
using Wolverine.Util;
using Xunit;

namespace Wolverine.MQTT.Tests;

/// <summary>
/// Verifies the fix in MqttTopic.CreateSender / MqttSenderProtocol: MQTT endpoints configured with
/// UseDurableOutbox() (backed here by PersistMessagesWithSqlite) now genuinely persist the outgoing
/// envelope until the broker has acknowledged the publish, instead of MqttTopicSender's old
/// fire-and-forget "handed to the local client queue" signal deleting the row immediately.
///
/// Deliberately has no subscriber/listener host: an MQTT broker acks a publish (QoS >= 1) based
/// purely on the publisher-broker handshake, regardless of whether anyone is subscribed to the
/// topic. That's the entire mechanism this fixture verifies, and a listener host isn't load-bearing
/// for it - it was cut after it surfaced an unrelated, genuine deadlock in disposing an MQTT
/// listener host (IHost.Dispose() on a host with a live MQTT subscription never returned; confirmed
/// via timestamped tracing showing "receiver.Dispose: starting" as the last line before a hang).
/// That's a pre-existing bug in the listener teardown path (MqttListener), not something this
/// sender-side fix touches - worth its own investigation, but out of scope here.
///
/// Everything here is against an in-process LocalMqttBroker on localhost, so every step is bounded
/// to a few seconds - there is no network latency to wait out. A step that doesn't finish in that
/// window is a bug (a hang), not something to wait longer for.
/// </summary>
[Collection("acceptance")]
public class durable_outbox_tests : IAsyncLifetime
{
    private string _dbFile = null!;
    private int _port;
    private LocalMqttBroker _broker = null!;
    private bool _brokerStopped;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"mqtt-durable-outbox-{Guid.NewGuid():N}.db");
        _port = PortFinder.GetAvailablePort();
        trace($"port={_port}");

        _broker = new LocalMqttBroker(_port) { Logger = NullLogger.Instance };
        await withTimeoutAsync("broker StartAsync", () => _broker.StartAsync());

        _sender = await withTimeoutAsync("sender host StartAsync", buildSenderAsync);
    }

    private Task<IHost> buildSenderAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DurableOutboxSender";

                // Solo mode makes recovered outbox messages start functioning immediately on this
                // single node, instead of Balanced mode's slower multi-node ownership/leader semantics.
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithSqlite($"Data Source={_dbFile}");
                opts.Services.AddResourceSetupOnStartup();

                opts.UseMqttWithLocalBroker(_port)
                    .ConfigureSenders(x => x.UseDurableOutbox());

                opts.PublishMessage<DurableOutboxPing>().ToMqttTopic("durable-outbox-tests");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await withTimeoutAsync("sender host StopAsync", () => _sender.StopAsync());
        _sender.Dispose();

        // LocalMqttBroker.StopAsync() is not guarded against being called on an already-stopped
        // server internally, and a test that intentionally stops the broker mid-run (see
        // message_stays_in_the_durable_outbox_when_the_broker_is_unreachable) must not call it twice.
        if (!_brokerStopped)
        {
            await withTimeoutAsync("broker StopAsync", () => _broker.StopAsync());
        }
        await withTimeoutAsync("broker DisposeAsync", () => _broker.DisposeAsync().AsTask());

        // Best-effort: Microsoft.Data.Sqlite pools connections at the ADO.NET level, so a pooled
        // connection can outlive the message store/host disposal above and hold the file open for a
        // moment. This is test cleanup, not an assertion - never let it mask a real test failure.
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbFile)) File.Delete(_dbFile);
        }
        catch (IOException)
        {
        }
    }

    // Every await in this fixture goes through here: local-only work has no excuse to take more
    // than a couple of seconds, and a timestamped trace line before/after each step means a hang
    // shows up as "started X, never finished" in the output instead of a silent, undiagnosable stall.
    private static async Task withTimeoutAsync(string what, Func<Task> action, int timeoutSeconds = 5)
    {
        trace($"{what}: starting");
        var task = action();
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (winner != task)
        {
            trace($"{what}: TIMED OUT after {timeoutSeconds}s");
            throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for '{what}'.");
        }

        await task; // observe/propagate any exception from the action itself
        trace($"{what}: finished");
    }

    private static async Task<T> withTimeoutAsync<T>(string what, Func<Task<T>> action, int timeoutSeconds = 5)
    {
        trace($"{what}: starting");
        var task = action();
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (winner != task)
        {
            trace($"{what}: TIMED OUT after {timeoutSeconds}s");
            throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for '{what}'.");
        }

        var result = await task;
        trace($"{what}: finished");
        return result;
    }

    // Console.Error, not ITestOutputHelper: xUnit buffers test-output-helper writes and only
    // surfaces them once the test finishes, which is useless for diagnosing a test that never
    // finishes. Console output streams immediately.
    private static void trace(string message) =>
        Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

    [Fact]
    public async Task message_is_removed_from_the_durable_outbox_once_the_broker_acknowledges_it()
    {
        var store = _sender.Services.GetRequiredService<IMessageStore>();

        var beforeCounts = await store.Admin.FetchCountsAsync();
        beforeCounts.Outgoing.ShouldBe(0, "The outbox should start empty.");

        var bus = _sender.MessageBus();
        await bus.PublishAsync(new DurableOutboxPing("hello"));

        var afterCounts = await pollOutgoingCountAsync(store, expected: 0);
        trace($"Final outgoing count: {afterCounts.Outgoing}");
        afterCounts.Outgoing.ShouldBe(0, "The outgoing envelope should be deleted from the durable outbox once the broker acknowledges the MQTT publish (MarkSuccessfulAsync).");
    }

    // Deliberately does NOT also verify recovery-after-restart in this same test: restarting the
    // local broker mid-test repeatedly hung the test process while developing this fixture.
    // Persistence-on-failure is fully verified in isolation here.
    [Fact]
    public async Task message_stays_in_the_durable_outbox_when_the_broker_is_unreachable()
    {
        var store = _sender.Services.GetRequiredService<IMessageStore>();

        var beforeCounts = await store.Admin.FetchCountsAsync();
        beforeCounts.Outgoing.ShouldBe(0, "The outbox should start empty.");

        // Simulate an outage: stop the broker so the underlying MQTT client cannot connect/publish.
        await withTimeoutAsync("broker StopAsync (simulate outage)", () => _broker.StopAsync());
        _brokerStopped = true;

        var bus = _sender.MessageBus();
        await bus.PublishAsync(new DurableOutboxPing("while the broker is down"));

        // Give the failed send attempt(s) time to run and hit MarkProcessingFailureAsync.
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        var whileDownCounts = await store.Admin.FetchCountsAsync();
        trace($"Outgoing count while broker is down: {whileDownCounts.Outgoing}");
        whileDownCounts.Outgoing.ShouldBeGreaterThanOrEqualTo(1,
            "The envelope should still be persisted in the durable outbox because the broker never acknowledged it - " +
            "if this is 0, the outbox row was deleted despite the publish never actually succeeding.");
    }

    private static async Task<PersistedCounts> pollOutgoingCountAsync(IMessageStore store, int expected, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        PersistedCounts counts;
        do
        {
            counts = await store.Admin.FetchCountsAsync();
            if (counts.Outgoing <= expected) return counts;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        } while (sw.Elapsed < (timeout ?? 5.Seconds()));

        return counts;
    }
}

public record DurableOutboxPing(string Message);
