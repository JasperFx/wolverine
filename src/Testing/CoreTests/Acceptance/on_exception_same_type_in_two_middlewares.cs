using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class on_exception_same_type_in_two_middlewares
{
    [Fact]
    public async Task both_handlers_run_in_registration_order()
    {
        var recorder = new SameTypeRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SameTypeTriggerHandler));
                opts.Policies.AddMiddleware(typeof(FirstSameTypeMiddleware));
                opts.Policies.AddMiddleware(typeof(SecondSameTypeMiddleware));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.InvokeMessageAndWaitAsync(new SameTypeTrigger());

        recorder.Calls.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task both_handlers_can_return_a_value()
    {
        var recorder = new SameTypeRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SameTypeTriggerHandler))
                    .IncludeType(typeof(SameTypeCascadeHandler));
                opts.Policies.AddMiddleware(typeof(FirstCascadingMiddleware));
                opts.Policies.AddMiddleware(typeof(SecondCascadingMiddleware));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .InvokeMessageAndWaitAsync(new SameTypeTrigger());

        recorder.Calls.ShouldBe(["first", "second"], ignoreOrder: true);
    }

    [Fact]
    public async Task both_handlers_can_return_a_continuation()
    {
        var recorder = new SameTypeRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SameTypeTriggerHandler));
                opts.Policies.AddMiddleware(typeof(FirstContinuationMiddleware));
                opts.Policies.AddMiddleware(typeof(SecondContinuationMiddleware));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.InvokeMessageAndWaitAsync(new SameTypeTrigger());

        recorder.Calls.ShouldBe(["first", "second"]);
    }
}

public static class FirstContinuationMiddleware
{
    public static HandlerContinuation OnException(ConcurrencyException ex, SameTypeRecorder recorder)
    {
        recorder.Calls.Add("first");
        return HandlerContinuation.Continue;
    }
}

public static class SecondContinuationMiddleware
{
    public static HandlerContinuation OnException(ConcurrencyException ex, SameTypeRecorder recorder)
    {
        recorder.Calls.Add("second");
        return HandlerContinuation.Continue;
    }
}

public record SameTypeCascade(string From);

public static class SameTypeCascadeHandler
{
    public static void Handle(SameTypeCascade message, SameTypeRecorder recorder) => recorder.Calls.Add(message.From);
}

public static class FirstCascadingMiddleware
{
    public static SameTypeCascade OnException(ConcurrencyException ex) => new("first");
}

public static class SecondCascadingMiddleware
{
    public static SameTypeCascade OnException(ConcurrencyException ex) => new("second");
}

public class SameTypeRecorder
{
    public List<string> Calls { get; } = new();
}

public record SameTypeTrigger;

public static class SameTypeTriggerHandler
{
    public static void Handle(SameTypeTrigger message) =>
        throw new ConcurrencyException("conflict", typeof(SameTypeTrigger), Guid.NewGuid());
}

public static class FirstSameTypeMiddleware
{
    public static void OnException(ConcurrencyException ex, SameTypeRecorder recorder) => recorder.Calls.Add("first");
}

public static class SecondSameTypeMiddleware
{
    public static void OnException(ConcurrencyException ex, SameTypeRecorder recorder) => recorder.Calls.Add("second");
}
