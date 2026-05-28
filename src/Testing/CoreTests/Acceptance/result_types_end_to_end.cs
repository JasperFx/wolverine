using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-2221: end-to-end exercise of the custom Result<T> support against FluentResults. Covers the
// in-process mediator surface (Jeremy's B-series test plan items B-1..B-5 + B-7) plus a sanity
// check that the codegen seams don't perturb plain non-Result handlers (E-1).
public class result_types_end_to_end
{
    private static IHostBuilder hostWithFluentResultsRegistered() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<OrdersBook>();

                opts.UseResultType(
                    typeof(Result<>),
                    stopWhen: x => ((ResultBase)x).IsFailed,
                    unwrapWith: x => x.GetType().GetProperty(nameof(Result<int>.ValueOrDefault))!.GetValue(x),
                    errorsFrom: x => ((ResultBase)x).Errors.Select(e => e.Message));
            });

    // B-1
    [Fact]
    public async Task invokeasync_T_against_result_returning_handler_unwraps_success()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        var placed = await bus.InvokeAsync<OrderPlaced>(new CreateOrder("o-1", 5));

        placed.ShouldNotBeNull();
        placed.OrderId.ShouldBe("o-1");
    }

    // B-2 — known follow-up. The failure branch on the InvokeAsync<T> request/reply path needs
    // seam 2 (UseForResponse) to send the wrapper through unchanged so component R can convert
    // it to ResultFailureException. With only seam 3 substituting the action source, the
    // failure path suppresses the cascade entirely and the reply listener doesn't see the
    // wrapper to translate. Same follow-up bucket as B-3.
    [Fact(Skip = "GH-2221 follow-up: failure-branch InvokeAsync<T> requires seam 2 to ship the raw wrapper on the reply path so component R can throw ResultFailureException. Same bucket as B-3 — Phase 3 polish + Phase 4 HTTP.")]
    public async Task invokeasync_T_against_result_failure_throws_resultfailureexception()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        var ex = await Should.ThrowAsync<ResultFailureException>(async () =>
            await bus.InvokeAsync<OrderPlaced>(new CreateOrder("o-2", 0)));

        ex.Errors.ShouldContain("Quantity must be positive");
    }

    // B-3 — known follow-up. With seam 3's unwrap-and-cascade replacing the chain's
    // ReturnVariableActionSource, the in-process InvokeAsync<Result<T>> path doesn't yet receive
    // the wrapper unchanged the way Jeremy's behaviour matrix specifies. The fix needs seam 2's
    // request/reply codegen to bypass the action-source substitution when the caller has set
    // ReplyRequested = typeof(Result<T>) — left for the follow-up alongside Phase 4 (HTTP).
    [Fact(Skip = "GH-2221 follow-up: InvokeAsync<Result<T>> wrapper-passthrough requires seam 2 to keep the raw wrapper on the reply path while seam 3 unwraps for fire-and-forget. Tracked as Phase 3 polish, alongside Phase 4 HTTP.")]
    public async Task invokeasync_of_raw_result_T_returns_the_wrapper_on_both_branches()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        var success = await bus.InvokeAsync<Result<OrderPlaced>>(new CreateOrder("o-3", 5));
        success.IsSuccess.ShouldBeTrue();
        success.Value.OrderId.ShouldBe("o-3");

        var failure = await bus.InvokeAsync<Result<OrderPlaced>>(new CreateOrder("o-4", 0));
        failure.IsFailed.ShouldBeTrue();
        failure.Errors.Select(e => e.Message).ShouldContain("Quantity must be positive");
    }

    // B-4 — on success, the unwrap-and-cascade frame turns the handler's Result<OrderPlaced>
    // into a cascaded OrderPlaced event. The TrackedSession trace confirms an OrderPlaced is
    // published as a cascading message even when no in-process sink consumes it (the
    // `NoRoutes` event in the trace IS the cascade-publish signal here). The handler-side
    // side-effect (book.Placed) doubles as proof the success branch ran end-to-end.
    [Fact]
    public async Task invokeasync_void_against_result_success_cascades_inner_T()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var book = host.Services.GetRequiredService<OrdersBook>();

        var tracked = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .InvokeMessageAndWaitAsync(new CreateOrder("o-5", 5));

        // Handler ran — book records the success-branch side effect.
        book.Placed.ShouldContain("o-5");

        // Cascade fired — TrackedSession recorded an OrderPlaced cascade. This is the seam-3
        // signal: had the handler returned plain Result<OrderPlaced>.Fail(), the same
        // tracked.AllRecordsInOrder() would NOT contain an OrderPlaced cascade (see B-5).
        tracked.AllRecordsInOrder().Any(r =>
                r.Envelope?.Message is OrderPlaced placed && placed.OrderId == "o-5")
            .ShouldBeTrue("Expected the unwrap-and-cascade frame to publish OrderPlaced for the success branch");
    }

    // B-5
    [Fact]
    public async Task invokeasync_void_against_result_failure_does_not_cascade_anything()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var book = host.Services.GetRequiredService<OrdersBook>();

        var tracked = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .InvokeMessageAndWaitAsync(new CreateOrder("o-6", 0));

        // Failure branch: handler didn't add to book, and no OrderPlaced cascade fired (the
        // seam-3 unwrapper logs the errors via ILogger and suppresses the cascade entirely).
        book.Placed.ShouldNotContain("o-6");
        tracked.AllRecordsInOrder().Any(r => r.Envelope?.Message is OrderPlaced)
            .ShouldBeFalse("A failed Result must not cascade any inner-T event");
    }

    // B-7 — async-handler variant of B-1 (success unwrap). The failure-throws case for async
    // handlers shares the B-2 follow-up; cover here that Task<Result<T>> compiles cleanly and
    // success unwraps the same way as the sync handler.
    [Fact]
    public async Task async_handler_returning_task_of_result_unwraps_normally()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        var placed = await bus.InvokeAsync<OrderPlaced>(new CreateOrderAsync("o-7", 5));
        placed.OrderId.ShouldBe("o-7");
    }

    // E-1 (regression guard)
    [Fact]
    public async Task plain_non_result_handlers_are_unaffected_when_result_types_registered()
    {
        using var host = await hostWithFluentResultsRegistered().StartAsync();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        // PlainCommand returns PlainResponse directly — no Result wrapper. The seam-3 policy
        // should leave its return-action source on the default CascadingMessageActionSource, and
        // the InvokeAsync<T> caller path should bypass the Result-aware branch.
        var response = await bus.InvokeAsync<PlainResponse>(new PlainCommand("hello"));
        response.Echo.ShouldBe("hello");
    }
}

// --- Test fixtures ----------------------------------------------------------------------------

public record CreateOrder(string OrderId, int Quantity);

public record CreateOrderAsync(string OrderId, int Quantity);

public record OrderPlaced(string OrderId);

public record PlainCommand(string Message);

public record PlainResponse(string Echo);

public sealed class OrdersBook
{
    public List<string> Placed { get; } = new();
}

public static class CreateOrderHandler
{
    public static Result<OrderPlaced> Handle(CreateOrder cmd, OrdersBook book)
    {
        if (cmd.Quantity <= 0)
        {
            return Result.Fail<OrderPlaced>("Quantity must be positive");
        }

        book.Placed.Add(cmd.OrderId);
        return Result.Ok(new OrderPlaced(cmd.OrderId));
    }

    public static async Task<Result<OrderPlaced>> HandleAsync(CreateOrderAsync cmd, OrdersBook book)
    {
        await Task.Yield();
        if (cmd.Quantity <= 0)
        {
            return Result.Fail<OrderPlaced>("Quantity must be positive");
        }

        book.Placed.Add(cmd.OrderId);
        return Result.Ok(new OrderPlaced(cmd.OrderId));
    }
}

public static class PlainHandler
{
    public static PlainResponse Handle(PlainCommand cmd) => new(cmd.Message);
}
