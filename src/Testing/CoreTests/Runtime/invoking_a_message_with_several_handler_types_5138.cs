using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5138. A message type handled by several handler types has one handler chain per handler type. Invoking it
/// inline runs every chain in turn, each in its own scope and transaction, and stops at the first failure.
/// Asking for a response runs only the one chain whose handler returns that response.
/// </summary>
public class invoking_a_message_with_several_handler_types_5138
{
    private static async Task<IHost> hostWith(params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);
            }).StartAsync();
    }

    [Fact]
    public async Task invoke_runs_every_handler_type_in_turn()
    {
        using var host = await hostWith(typeof(FirstInvokedHandler5138), typeof(SecondInvokedHandler5138));
        Invoked5138.Clear();

        var message = new InvokedMessage5138(Guid.NewGuid(), FailIn: null);
        await host.MessageBus().InvokeAsync(message, TestContext.Current.CancellationToken);

        Invoked5138.Received.ShouldBe([typeof(FirstInvokedHandler5138), typeof(SecondInvokedHandler5138)]);
    }

    [Fact]
    public async Task invoke_stops_at_the_first_failure_and_names_the_handler_type()
    {
        using var host = await hostWith(typeof(FirstInvokedHandler5138), typeof(SecondInvokedHandler5138),
            typeof(ThirdInvokedHandler5138));
        Invoked5138.Clear();

        var message = new InvokedMessage5138(Guid.NewGuid(), FailIn: typeof(SecondInvokedHandler5138));
        var ex = await Should.ThrowAsync<HandlerInvocationException>(() => host.MessageBus().InvokeAsync(message));

        ex.MessageType.ShouldBe(typeof(InvokedMessage5138));
        ex.HandlerType.ShouldBe(typeof(SecondInvokedHandler5138));
        ex.InnerException.ShouldBeOfType<DivideByZeroException>();
        ex.Message.ShouldContain($"Handler type {typeof(SecondInvokedHandler5138).FullNameInCode()} failed");
        ex.Message.ShouldContain($"Already completed: {typeof(FirstInvokedHandler5138).FullNameInCode()}");
        ex.Message.ShouldContain($"Not run: {typeof(ThirdInvokedHandler5138).FullNameInCode()}");

        // The first handler type ran, the third did not
        Invoked5138.Received.ShouldBe([typeof(FirstInvokedHandler5138), typeof(SecondInvokedHandler5138)]);
    }

    [Fact]
    public async Task each_handler_type_runs_with_an_envelope_of_its_own()
    {
        using var host = await hostWith(typeof(FirstInvokedHandler5138), typeof(SecondInvokedHandler5138));
        Invoked5138.Clear();

        var session = await host.InvokeMessageAndWaitAsync(new InvokedMessage5138(Guid.NewGuid(), FailIn: null));

        // One execution per handler type, each of a distinct envelope
        session.Executed.Envelopes().Where(x => x.Message is InvokedMessage5138).Select(x => x.Id).Distinct()
            .Count().ShouldBe(2);
    }

    [Fact]
    public async Task a_single_handler_type_is_not_wrapped()
    {
        using var host = await hostWith(typeof(FirstInvokedHandler5138));
        Invoked5138.Clear();

        var message = new InvokedMessage5138(Guid.NewGuid(), FailIn: typeof(FirstInvokedHandler5138));
        await Should.ThrowAsync<DivideByZeroException>(() => host.MessageBus().InvokeAsync(message));
    }

    [Fact]
    public async Task invoke_for_a_response_runs_only_the_handler_type_that_returns_it()
    {
        using var host = await hostWith(typeof(AnsweringHandler5138), typeof(AuditingHandler5138));
        Invoked5138.Clear();

        var answer = await host.MessageBus().InvokeAsync<Answer5138>(new Question5138("what"), TestContext.Current.CancellationToken);

        answer.Text.ShouldBe("answer to what");
        Invoked5138.Received.ShouldBe([typeof(AnsweringHandler5138)]);
    }

    [Fact]
    public async Task a_response_returned_in_a_tuple_or_a_task_is_found()
    {
        using var host = await hostWith(typeof(TupleAnsweringHandler5138), typeof(TaskAnsweringHandler5138));
        Invoked5138.Clear();

        var bus = host.MessageBus();

        (await bus.InvokeAsync<Answer5138>(new TupleQuestion5138("one"), TestContext.Current.CancellationToken)).Text.ShouldBe("tuple one");
        (await bus.InvokeAsync<Receipt5138>(new TupleQuestion5138("two"), TestContext.Current.CancellationToken)).Text.ShouldBe("task two");

        Invoked5138.Received.ShouldBe([typeof(TupleAnsweringHandler5138), typeof(TaskAnsweringHandler5138)]);
    }

    [Fact]
    public async Task two_handler_types_returning_the_response_is_an_ambiguity_error()
    {
        using var host = await hostWith(typeof(AnsweringHandler5138), typeof(SecondAnsweringHandler5138));

        var ex = await Should.ThrowAsync<AmbiguousHandlerChainException>(() =>
            host.MessageBus().InvokeAsync<Answer5138>(new Question5138("what")));

        ex.MessageType.ShouldBe(typeof(Question5138));
        ex.Message.ShouldContain(typeof(AnsweringHandler5138).FullNameInCode());
        ex.Message.ShouldContain(typeof(SecondAnsweringHandler5138).FullNameInCode());
        ex.Message.ShouldContain("all do");
    }

    [Fact]
    public async Task no_handler_type_returning_the_response_is_an_error_too()
    {
        using var host = await hostWith(typeof(AnsweringHandler5138), typeof(AuditingHandler5138));

        var ex = await Should.ThrowAsync<AmbiguousHandlerChainException>(() =>
            host.MessageBus().InvokeAsync<Receipt5138>(new Question5138("what")));

        ex.Message.ShouldContain("none of them declares that return type");
    }

    [Fact]
    public async Task stream_runs_only_the_handler_type_that_streams_the_response()
    {
        using var host = await hostWith(typeof(StreamingHandler5138), typeof(StreamAuditingHandler5138));
        Invoked5138.Clear();

        var answers = new List<Answer5138>();
        await foreach (var answer in host.MessageBus().StreamAsync<Answer5138>(new StreamQuestion5138(3), TestContext.Current.CancellationToken))
        {
            answers.Add(answer);
        }

        answers.Select(x => x.Text).ShouldBe(["0", "1", "2"]);
        Invoked5138.Received.ShouldBe([typeof(StreamingHandler5138)]);
    }
}

public record InvokedMessage5138(Guid Id, Type? FailIn);

public record Question5138(string Text);

public record TupleQuestion5138(string Text);

public record StreamQuestion5138(int Count);

public record Answer5138(string Text);

public record Receipt5138(string Text);

public static class Invoked5138
{
    public static readonly List<Type> Received = [];

    public static void Clear()
    {
        lock (Received) Received.Clear();
    }

    public static void Record(object handler)
    {
        lock (Received) Received.Add(handler.GetType());
    }

    public static void RecordAndMaybeFail(object handler, InvokedMessage5138 message)
    {
        Record(handler);
        if (message.FailIn == handler.GetType()) throw new DivideByZeroException();
    }
}

// Hidden from conventional discovery so they cannot leak into every other host in this assembly
[WolverineIgnore]
public class FirstInvokedHandler5138
{
    public void Handle(InvokedMessage5138 message) => Invoked5138.RecordAndMaybeFail(this, message);
}

[WolverineIgnore]
public class SecondInvokedHandler5138
{
    public void Handle(InvokedMessage5138 message) => Invoked5138.RecordAndMaybeFail(this, message);
}

[WolverineIgnore]
public class ThirdInvokedHandler5138
{
    public void Handle(InvokedMessage5138 message) => Invoked5138.RecordAndMaybeFail(this, message);
}

[WolverineIgnore]
public class AnsweringHandler5138
{
    public Answer5138 Handle(Question5138 question)
    {
        Invoked5138.Record(this);
        return new Answer5138("answer to " + question.Text);
    }
}

[WolverineIgnore]
public class SecondAnsweringHandler5138
{
    public Answer5138 Handle(Question5138 question)
    {
        Invoked5138.Record(this);
        return new Answer5138("another answer to " + question.Text);
    }
}

[WolverineIgnore]
public class AuditingHandler5138
{
    public void Handle(Question5138 question) => Invoked5138.Record(this);
}

[WolverineIgnore]
public class TupleAnsweringHandler5138
{
    public (Answer5138, InvokedMessage5138?) Handle(TupleQuestion5138 question)
    {
        Invoked5138.Record(this);
        return (new Answer5138("tuple " + question.Text), null);
    }
}

[WolverineIgnore]
public class TaskAnsweringHandler5138
{
    public async Task<Receipt5138> Handle(TupleQuestion5138 question)
    {
        await Task.Yield();
        Invoked5138.Record(this);
        return new Receipt5138("task " + question.Text);
    }
}

[WolverineIgnore]
public class StreamingHandler5138
{
    public async IAsyncEnumerable<Answer5138> Handle(StreamQuestion5138 question)
    {
        Invoked5138.Record(this);
        for (var i = 0; i < question.Count; i++)
        {
            await Task.Yield();
            yield return new Answer5138(i.ToString());
        }
    }
}

[WolverineIgnore]
public class StreamAuditingHandler5138
{
    public void Handle(StreamQuestion5138 question) => Invoked5138.Record(this);
}
