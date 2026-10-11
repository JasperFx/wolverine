using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4955. On an inline JetStream endpoint with no sending failure policy, a publish the server refuses used to be
/// retried three times by the inline sending agent, logged as "Error while trying to retry", discarded, and
/// SendAsync() returned as if the message had been sent. Now the refusal is not retried and SendAsync() throws it.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class Gh4955_inline_send_rejection
{
    private readonly ITestOutputHelper _output;

    public Gh4955_inline_send_rejection(ITestOutputHelper output) => _output = output;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_refused_inline_publish_fails_the_send_without_retries()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var stream = $"REJECT_INLINE_{Guid.NewGuid():N}";
        var subject = $"reject.inline.{Guid.NewGuid():N}";
        var logs = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output).AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "RejectedInlinePublish";
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s =>
                    {
                        s.WithSubjects(subject);
                        s.MaxMessages = 1;
                        s.DiscardPolicy = StreamConfigDiscard.New;
                    });

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream).SendInline();
            })
            .StartAsync(cancellationToken: Ct);

        var bus = host.MessageBus();

        // fills the stream to its one-message limit
        await bus.SendAsync(new OrderPlaced(Guid.NewGuid().ToString("N")));

        // the server refuses this one, and the caller hears about it
        var rejection = await Should.ThrowAsync<SendRejectedException>(() =>
            bus.SendAsync(new OrderPlaced(Guid.NewGuid().ToString("N"))).AsTask());

        var apiError = rejection.InnerException.ShouldBeOfType<NatsJSApiException>();
        _output.WriteLine($"refused: {rejection.Message} / {apiError.Error.Description} ({apiError.Error.ErrCode})");
        apiError.Error.ErrCode.ShouldBe(10077); // a full stream comes back as the generic "store failed" error

        // ...and it was not retried on the way
        logs.Messages.ShouldNotContain(m => m.Contains("Error while trying to retry"));
        logs.Messages.ShouldNotContain(m => m.Contains("Discarding message"));
        logs.Messages.ShouldContain(m => m.Contains("refused message"));

        // the first message is still the only one in the stream, and the host is still usable
        (await Bug_4845_silent_jetstream_loss.CountStreamMessagesAsync(natsUrl, stream)).ShouldBe(1);
    }

    // The codes nats-server 2.14 answers each refused publish with, probed against a live server
    [Theory]
    [InlineData(503, 10077, "maximum messages exceeded", true)]
    [InlineData(503, 10077, "maximum bytes exceeded", true)]
    [InlineData(503, 10077, "maximum messages per subject exceeded", true)]
    [InlineData(400, 10054, "message size exceeds maximum allowed", true)]
    [InlineData(400, 10071, "wrong last sequence: 1", true)]
    [InlineData(400, 10060, "expected stream does not match", true)]
    [InlineData(400, 10070, "wrong last msg ID: ", true)]
    [InlineData(503, 10077, "store closed", false)]
    [InlineData(503, 10039, "jetstream not enabled", false)]
    [InlineData(500, 10049, "stream not found", false)]
    public void classifies_jetstream_refusals(int code, int errCode, string description, bool isRejection)
    {
        JetStreamPublisher.IsRejection(new ApiError { Code = code, ErrCode = errCode, Description = description })
            .ShouldBe(isRejection);
    }

    private class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider.Messages)
                {
                    provider.Messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
