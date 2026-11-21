using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests.ErrorHandling;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.ErrorHandling;

public class dead_letter_interceptor : ErrorHandlingContext
{
    private readonly RecordingDeadLetterInterceptor _first;
    private readonly RecordingDeadLetterInterceptor _second;

    public dead_letter_interceptor()
    {
        // The first interceptor swaps in a replacement exception; the second just records what it
        // receives - which proves both that interceptors run and that the return value is threaded
        // from one to the next before the envelope is handed to durable storage.
        _first = new RecordingDeadLetterInterceptor(replaceWith: new RedactedException());
        _second = new RecordingDeadLetterInterceptor();

        ConfigureOptions(opts =>
        {
            opts.Services.AddSingleton<IDeadLetterInterceptor>(_first);
            opts.Services.AddSingleton<IDeadLetterInterceptor>(_second);

            opts.HandlerGraph.ConfigureHandlerForMessage<ErrorCausingMessage>(chain =>
            {
                chain.OnException<DivideByZeroException>().MoveToErrorQueue();
                chain.Failures.MaximumAttempts = 3;
            });
        });
    }

    [Fact]
    public async Task interceptors_run_before_storage_and_chain_the_exception()
    {
        throwOnAttempt<DivideByZeroException>(1);

        await shouldMoveToErrorQueueOnAttempt(1);

        // Both registered interceptors were invoked on the dead-letter path.
        _first.Invoked.ShouldBeTrue();
        _second.Invoked.ShouldBeTrue();

        // The first interceptor sees the original failure exception and the failed envelope.
        _first.ReceivedException.ShouldBeOfType<DivideByZeroException>();
        _first.ReceivedEnvelope!.Message.ShouldBeOfType<ErrorCausingMessage>();

        // The replacement returned by the first interceptor is what the second one receives,
        // i.e. the return value is threaded through and would be persisted in place of the original.
        _second.ReceivedException.ShouldBeOfType<RedactedException>();
    }
}

internal sealed class RedactedException : Exception
{
    public RedactedException() : base("redacted")
    {
    }
}

internal sealed class RecordingDeadLetterInterceptor : IDeadLetterInterceptor
{
    private readonly Exception? _replaceWith;

    public RecordingDeadLetterInterceptor(Exception? replaceWith = null)
    {
        _replaceWith = replaceWith;
    }

    public bool Invoked { get; private set; }
    public Envelope? ReceivedEnvelope { get; private set; }
    public Exception? ReceivedException { get; private set; }

    public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception, CancellationToken cancellation)
    {
        Invoked = true;
        ReceivedEnvelope = envelope;
        ReceivedException = exception;
        return new ValueTask<Exception?>(_replaceWith ?? exception);
    }
}

public class dead_letter_interception_without_a_message
{
    [Fact]
    public async Task interceptors_are_skipped_when_the_envelope_has_no_message()
    {
        // An envelope that could not be deserialized (or unwrapped) has a raw body and no message. There is nothing
        // to redact, and interceptors written against the handler's message should never see a null one
        var interceptor = new RecordingDeadLetterInterceptor(replaceWith: new RedactedException());

        var runtime = Substitute.For<Wolverine.Runtime.IWolverineRuntime>();
        runtime.Services.Returns(new ServiceCollection()
            .AddSingleton<IDeadLetterInterceptor>(interceptor)
            .BuildServiceProvider());

        var original = new InvalidOperationException("could not deserialize");
        var envelope = new Envelope { Data = [1, 2, 3], MessageType = "unknown" };

        var persisted = await DeadLetterInterception.InterceptAsync(runtime, envelope, original);

        interceptor.Invoked.ShouldBeFalse();
        persisted.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task interceptors_run_when_the_envelope_has_a_message()
    {
        var interceptor = new RecordingDeadLetterInterceptor(replaceWith: new RedactedException());

        var runtime = Substitute.For<Wolverine.Runtime.IWolverineRuntime>();
        runtime.Services.Returns(new ServiceCollection()
            .AddSingleton<IDeadLetterInterceptor>(interceptor)
            .BuildServiceProvider());

        var envelope = new Envelope(new ErrorCausingMessage());

        var persisted = await DeadLetterInterception.InterceptAsync(runtime, envelope, new DivideByZeroException());

        interceptor.Invoked.ShouldBeTrue();
        persisted.ShouldBeOfType<RedactedException>();
    }
}
