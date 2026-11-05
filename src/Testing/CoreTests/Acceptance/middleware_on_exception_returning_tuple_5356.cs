using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-5356. A middleware OnException returning a tuple generated "(var a, var b)" as the argument of
// EnqueueCascadingAsync, which does not compile. Asserts the cascaded message is HANDLED, not just sent.
public class middleware_on_exception_returning_tuple_5356
{
    [Fact]
    public async Task tuple_returned_from_middleware_on_exception_cascades_each_element()
    {
        var recorder = new TupleOnExceptionRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TupleThrowingHandler))
                    .IncludeType(typeof(TupleCascadeHandler))
                    .IncludeType(typeof(TupleOutgoingHandler));
                opts.Policies.AddMiddleware(typeof(TupleOnExceptionMiddleware),
                    chain => chain.MessageType == typeof(TupleThrowing));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new TupleThrowing("a"));

        recorder.Actions.ShouldContain("cascade:a");
        recorder.Actions.ShouldContain("outgoing:a");
    }
}

public record TupleThrowing(string Name);
public record TupleCascade(string Name);
public record TupleOutgoing(string Name);

public class TupleOnExceptionRecorder
{
    public List<string> Actions { get; } = new();
}

public static class TupleThrowingHandler
{
    public static void Handle(TupleThrowing message) => throw new ConcurrencyException(typeof(string), "x");
}

public static class TupleCascadeHandler
{
    public static void Handle(TupleCascade message, TupleOnExceptionRecorder recorder) =>
        recorder.Actions.Add("cascade:" + message.Name);
}

public static class TupleOutgoingHandler
{
    public static void Handle(TupleOutgoing message, TupleOnExceptionRecorder recorder) =>
        recorder.Actions.Add("outgoing:" + message.Name);
}

public static class TupleOnExceptionMiddleware
{
    public static (TupleCascade, OutgoingMessages) OnException(ConcurrencyException ex, TupleThrowing message) =>
        (new TupleCascade(message.Name), [new TupleOutgoing(message.Name)]);
}
