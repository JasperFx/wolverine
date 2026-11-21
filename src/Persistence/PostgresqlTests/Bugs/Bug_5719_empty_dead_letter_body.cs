using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Xunit;

namespace PostgresqlTests.Bugs;

// GH-5719. A dead letter whose envelope cannot be serialized (no Message and no Data) is stored with an empty body,
// Wolverine's own "could not serialize" marker. Reading it back used to hand that empty body to EnvelopeSerializer,
// which threw EndOfStreamException, and every read of the queue logged an Error for that row -- forever, for a
// body Wolverine itself chose to write empty. The row should read back as the usual placeholder, quietly.
[Collection("marten")]
public class Bug_5719_empty_dead_letter_body : IAsyncLifetime
{
    private readonly RecordingLoggerProvider _logs = new();
    private IHost _host = null!;
    private IWolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dlq_5719");
                opts.Services.AddSingleton<ILoggerProvider>(_logs);
            }).StartAsync();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        await _runtime.Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_dead_letter_stored_with_an_empty_body_reads_back_as_a_placeholder_without_an_error()
    {
        // No Message and no Data, so the dead letter write cannot serialize it and stores an empty body
        var envelope = new Envelope
        {
            MessageType = "Any.Message.Type",
            Destination = new Uri("local://repro")
        };

        await _runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        // The write side reports the serialization failure once, and says the dead letter was still recorded
        _logs.Errors.ShouldContain(x => x.Contains("still recorded, with an empty body"));

        var query = new DeadLetterEnvelopeQuery { PageNumber = 0, PageSize = 50, Range = TimeRange.AllTime() };

        // Polled twice, the way a dead letter UI or monitor would
        for (var i = 0; i < 2; i++)
        {
            var results = await _runtime.Storage.DeadLetters.QueryAsync(query, CancellationToken.None);

            var deadLetter = results.Envelopes.ShouldHaveSingleItem();
            deadLetter.Id.ShouldBe(envelope.Id);
            deadLetter.MessageType.ShouldBe("Any.Message.Type");
            deadLetter.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
            deadLetter.Envelope.Message.ShouldBeOfType<PlaceHolder>();
            deadLetter.Envelope.Destination.ShouldBe(new Uri("local://repro"));
        }

        var single = await _runtime.Storage.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);
        single.ShouldNotBeNull();
        single.Envelope.Message.ShouldBeOfType<PlaceHolder>();

        // Before the fix, every one of those reads logged "Unable to deserialize the stored body ..." at Error
        _logs.Errors.ShouldNotContain(x => x.Contains("Unable to deserialize the stored body"));
    }

    private class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _errors = new();

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_errors) return _errors.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private class RecordingLogger(RecordingLoggerProvider parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error) return;

                lock (parent._errors) parent._errors.Add(formatter(state, exception));
            }
        }
    }
}
