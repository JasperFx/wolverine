using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A message moved to the error queue has to end up in exactly one place: stored on the dead-letter subject, or
/// still held by the source consumer. The copy used to carry every header of the original, so the dead-letter
/// stream applied the producer's <c>Nats-Expected-*</c>, <c>Nats-TTL</c> and <c>Nats-Rollup</c> headers to itself; it
/// went out as a core publish nobody confirmed; and the original was terminated either way. Now the copy drops
/// the headers the server acts on, it is a JetStream publish whose PubAck is checked, and the original is only
/// terminated once the copy is stored -- otherwise it is handed back to JetStream for redelivery, except on a
/// Buffered listener, which acknowledged it on receipt and can only report the loss.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsDeadLetterForwardingTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsDeadLetterForwardingTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task the_copy_does_not_carry_the_expected_stream_of_the_original()
    {
        var names = new Names();
        await createStreamsAsync(names);

        using var host = await startAsync(names, EndpointMode.BufferedInMemory);
        await publishAsync(host, names, "A", new() { ["Nats-Expected-Stream"] = names.SourceStream });

        await waitForDeadLettersAsync(names, 1);
        var copy = await deadLetterHeadersAsync(names);
        copy.ShouldNotContainKey("Nats-Expected-Stream");

        // The diagnostics of the delivery stay
        copy["nats-stream"].ToString().ShouldBe(names.SourceStream);
    }

    [Fact]
    public async Task the_publish_hook_does_not_reach_the_copy()
    {
        // ConfigureJetStreamPublish shapes the application's own publishes. A hook pinning them to their stream
        // gave every dead-letter copy the wrong expected stream, and the dead-letter stream refused all of them
        var names = new Names();
        await createStreamsAsync(names);

        using var host = await startAsync(names, EndpointMode.BufferedInMemory,
            configurePublish: (_, pubOpts) => pubOpts with { ExpectedStream = names.SourceStream });
        await publishAsync(host, names, "A");

        await waitForDeadLettersAsync(names, 1);
    }

    [Fact]
    public async Task the_copy_does_not_carry_the_message_ttl_of_the_original()
    {
        var names = new Names();

        // Only the source stream allows per-message TTLs, so the dead-letter stream refused a copy carrying one
        await createStreamsAsync(names, source => source.AllowMsgTTL = true);

        using var host = await startAsync(names, EndpointMode.BufferedInMemory);
        await publishAsync(host, names, "A", new() { ["Nats-TTL"] = "1h" });

        await waitForDeadLettersAsync(names, 1);
        (await deadLetterHeadersAsync(names)).ShouldNotContainKey("Nats-TTL");
    }

    [Fact]
    public async Task a_rollup_on_the_original_does_not_purge_earlier_dead_letters()
    {
        var names = new Names();
        await createStreamsAsync(names, source => source.AllowRollupHdrs = true,
            deadLetters => deadLetters.AllowRollupHdrs = true);

        using var host = await startAsync(names, EndpointMode.BufferedInMemory);
        await publishAsync(host, names, "A");
        await waitForDeadLettersAsync(names, 1);

        await publishAsync(host, names, "B", new() { ["Nats-Rollup"] = "sub" });

        await waitForDeadLettersAsync(names, 2);
        (await countAsync(names.DeadLetterStream)).ShouldBe(2);
    }

    [Fact]
    public async Task an_inline_listener_keeps_the_original_when_the_dead_letter_stream_refuses_the_copy()
    {
        var names = new Names();

        // A full dead-letter stream under DiscardPolicy.New refuses every further copy
        await createStreamsAsync(names, configureDeadLetters: deadLetters =>
        {
            deadLetters.MaxMsgs = 1;
            deadLetters.Discard = StreamConfigDiscard.New;
        });
        await using (var connection = await connectAsync())
        {
            await connection.CreateJetStreamContext().PublishAsync(names.DeadLetterSubject, new byte[] { 1 },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        using var host = await startAsync(names, EndpointMode.Inline);
        await publishAsync(host, names, "A");

        // Handed back to JetStream instead of being terminated, so it is delivered again
        await waitForAttemptsAsync(names, "A", 2, 20.Seconds());
        DeadLetterProbeHandler.AttemptsOf(names.RunId, "A").ShouldBeGreaterThanOrEqualTo(2);
        (await countAsync(names.DeadLetterStream)).ShouldBe(1);
    }

    [Fact]
    public async Task an_inline_listener_keeps_the_original_when_no_stream_captures_the_dead_letter_subject()
    {
        var names = new Names();
        await createStreamsAsync(names, createDeadLetterStream: false);

        using var host = await startAsync(names, EndpointMode.Inline);
        await publishAsync(host, names, "A");

        await waitForAttemptsAsync(names, "A", 2, 20.Seconds());
        DeadLetterProbeHandler.AttemptsOf(names.RunId, "A").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task a_buffered_listener_reports_a_lost_message_at_error_level()
    {
        var names = new Names();
        await createStreamsAsync(names, createDeadLetterStream: false);
        var logs = new ErrorRecorder();

        using var host = await startAsync(names, EndpointMode.BufferedInMemory, logs);
        var id = await publishAsync(host, names, "A");

        // The Buffered listener acknowledged the delivery on receipt, so there is nothing to hand back
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (DateTimeOffset.UtcNow < deadline &&
               !logs.Errors.Any(x => x.Contains(id.ToString()) && x.Contains("lost")))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        logs.Errors.ShouldContain(x => x.Contains(id.ToString()) && x.Contains("lost"));
    }

    private async Task createStreamsAsync(Names names, Action<StreamConfig>? configureSource = null,
        Action<StreamConfig>? configureDeadLetters = null, bool createDeadLetterStream = true)
    {
        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        var source = new StreamConfig(names.SourceStream, [$"{names.Root}.>"]);
        configureSource?.Invoke(source);
        await js.CreateStreamAsync(source, TestContext.Current.CancellationToken);

        if (createDeadLetterStream)
        {
            var deadLetters = new StreamConfig(names.DeadLetterStream, [names.DeadLetterSubject]);
            configureDeadLetters?.Invoke(deadLetters);
            await js.CreateStreamAsync(deadLetters, TestContext.Current.CancellationToken);
        }
    }

    private Task<IHost> startAsync(Names names, EndpointMode mode, ILoggerProvider? logs = null,
        Func<Envelope, NatsJSPubOpts, NatsJSPubOpts>? configurePublish = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l =>
            {
                l.AddXunitLogging(_output);
                if (logs != null)
                {
                    l.AddProvider(logs);
                }
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DeadLetterForwarding";
                var nats = opts.UseNats(_fixture.ConnectionString);
                if (configurePublish != null)
                {
                    nats.ConfigureJetStreamPublish(configurePublish);
                }

                opts.Policies.DisableConventionalLocalRouting();
                opts.Policies.OnException<DeadLetterProbeException>().MoveToErrorQueue();

                opts.PublishMessage<DeadLetterProbe>().ToNatsSubject(names.Subject).UseJetStream(names.SourceStream);

                var listener = opts.ListenToNatsSubject(names.Subject)
                    .UseJetStream(names.SourceStream, names.Consumer)
                    .DeadLetterTo(names.DeadLetterSubject);

                if (mode == EndpointMode.Inline)
                {
                    listener.ProcessInline();
                }
                else
                {
                    listener.BufferedInMemory();
                }
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> publishAsync(IHost host, Names names, string name,
        Dictionary<string, string?>? headers = null)
    {
        var options = new DeliveryOptions();
        foreach (var (key, value) in headers ?? [])
        {
            options.Headers[key] = value;
        }

        DeadLetterProbeHandler.Expect(names.RunId, name);

        await host.MessageBus().PublishAsync(new DeadLetterProbe(names.RunId, name), options);
        return await DeadLetterProbeHandler.EnvelopeIdOfAsync(names.RunId, name, 10.Seconds());
    }

    private async Task waitForDeadLettersAsync(Names names, long count)
    {
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (DateTimeOffset.UtcNow < deadline && await countAsync(names.DeadLetterStream) < count)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        (await countAsync(names.DeadLetterStream)).ShouldBe(count);
    }

    private static async Task waitForAttemptsAsync(Names names, string name, int attempts, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && DeadLetterProbeHandler.AttemptsOf(names.RunId, name) < attempts)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The headers of the first message on the dead-letter stream
    /// </summary>
    private async Task<NatsHeaders> deadLetterHeadersAsync(Names names)
    {
        await using var connection = await connectAsync();
        var reader = $"reader-{Guid.NewGuid():N}";
        var consumer = await connection.CreateJetStreamContext().CreateOrUpdateConsumerAsync(
            names.DeadLetterStream,
            new ConsumerConfig { Name = reader, DurableName = reader, AckPolicy = ConsumerConfigAckPolicy.None },
            TestContext.Current.CancellationToken);
        var message = await consumer.NextAsync<byte[]>(cancellationToken: TestContext.Current.CancellationToken);

        var headers = message.ShouldNotBeNull().Headers ?? new NatsHeaders();
        _output.WriteLine(string.Join(Environment.NewLine, headers.Select(x => $"{x.Key}: {x.Value}")));
        return headers;
    }

    private async Task<long> countAsync(string stream)
    {
        await using var connection = await connectAsync();
        try
        {
            return (await connection.CreateJetStreamContext()
                .GetStreamAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Info.State.Messages;
        }
        catch (NatsJSApiException)
        {
            return 0;
        }
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }

    private sealed class Names
    {
        public string RunId { get; } = Guid.NewGuid().ToString("N")[..12];
        public string SourceStream => $"DLQSRC_{RunId}";
        public string DeadLetterStream => $"DLQDST_{RunId}";
        public string Root => $"dlqsrc.{RunId}";
        public string Subject => $"{Root}.in";
        public string DeadLetterSubject => $"dlqdst.{RunId}";
        public string Consumer => $"dlqsrc-{RunId}";
    }

    private sealed class ErrorRecorder : ILoggerProvider, ILogger
    {
        private readonly List<string> _errors = new();

        public string[] Errors
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;

            lock (_errors)
            {
                _errors.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}

public record DeadLetterProbe(string RunId, string Name);

public class DeadLetterProbeException(string message) : Exception(message)
{
    // One line: a Windows stack trace has CRLF, which NATS.Net refuses in the exception-stack header of the copy,
    // a separate problem these tests must not trip over
    public override string StackTrace => "at DeadLetterProbeHandler.Handle";
}

public static class DeadLetterProbeHandler
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<Guid>> _envelopeIds = new();

    public static void Expect(string runId, string name)
    {
        _envelopeIds[$"{runId}/{name}"] =
            new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static Task<Guid> EnvelopeIdOfAsync(string runId, string name, TimeSpan timeout)
    {
        return _envelopeIds[$"{runId}/{name}"].Task.WaitAsync(timeout);
    }

    public static int AttemptsOf(string runId, string name) =>
        _attempts.TryGetValue($"{runId}/{name}", out var attempts) ? attempts : 0;

    public static void Handle(DeadLetterProbe message, Envelope envelope)
    {
        var key = $"{message.RunId}/{message.Name}";
        _attempts.AddOrUpdate(key, 1, (_, x) => x + 1);
        if (_envelopeIds.TryGetValue(key, out var id))
        {
            id.TrySetResult(envelope.Id);
        }

        throw new DeadLetterProbeException($"This message is poison: {key}");
    }
}
