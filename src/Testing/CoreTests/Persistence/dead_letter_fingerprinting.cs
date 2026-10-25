using System.Runtime.CompilerServices;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Persistence;

public class dead_letter_fingerprinting
{
    private readonly DefaultDeadLetterFingerprinter theFingerprinter = new();

    private static Exception capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("Did not throw");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void approvePayment(string detail) =>
        throw new InvalidOperationException($"Payment for {detail} was refused");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void reserveStock(string detail) =>
        throw new InvalidOperationException($"No stock for {detail}");

    private DeadLetterFingerprint fingerprint(Exception exception) =>
        theFingerprinter.Fingerprint(new Envelope(), exception);

    [Fact]
    public void the_same_failure_with_different_data_in_the_message_is_one_group()
    {
        var one = fingerprint(capture(() => approvePayment("order 1")));
        var two = fingerprint(capture(() => approvePayment(Guid.NewGuid().ToString())));

        one.Group.ShouldBe(two.Group);
        one.Title.ShouldBe(two.Title);
    }

    [Fact]
    public void the_same_exception_type_from_different_code_is_a_different_group()
    {
        var payment = fingerprint(capture(() => approvePayment("1")));
        var stock = fingerprint(capture(() => reserveStock("1")));

        payment.Group.ShouldNotBe(stock.Group);
    }

    [Fact]
    public void the_group_is_a_stable_hash()
    {
        var group = fingerprint(capture(() => approvePayment("1"))).Group;

        group.Length.ShouldBe(32);
        group.ShouldBe(fingerprint(capture(() => approvePayment("2"))).Group);
    }

    [Fact]
    public void the_title_names_the_exception_type_and_the_method()
    {
        fingerprint(capture(() => approvePayment("1"))).Title
            .ShouldBe("System.InvalidOperationException: dead_letter_fingerprinting.approvePayment(...)");
    }

    private const string StackWithFrameworkFrames = """
           at System.Threading.Tasks.ValueTask.ThrowIfCompletedUnsuccessfully()
           at Wolverine.Runtime.Handlers.Executor.ExecuteAsync(MessageContext context, CancellationToken cancellation)
           at Internal.Generated.WolverineHandlers.PlaceOrderHandler1234.HandleAsync(MessageContext context, CancellationToken cancellation)
           at lambda_method12(Closure, Object)
           at MyApp.Orders.OrderHandler.Handle(PlaceOrder command) in /src/Orders/OrderHandler.cs:line 22
           at MyApp.Orders.OrderService.Place() in /src/Orders/OrderService.cs:line 10
        """;

    [Fact]
    public void framework_frames_are_skipped()
    {
        theFingerprinter.FindApplicationFrame(StackWithFrameworkFrames)
            .ShouldBe("MyApp.Orders.OrderHandler.Handle(PlaceOrder command)");
    }

    [Fact]
    public void line_numbers_do_not_split_a_group()
    {
        var moved = StackWithFrameworkFrames.Replace("line 22", "line 57");

        theFingerprinter.FindApplicationFrame(moved)
            .ShouldBe(theFingerprinter.FindApplicationFrame(StackWithFrameworkFrames));
    }

    [Fact]
    public void the_skipped_namespaces_are_configurable()
    {
        theFingerprinter.FrameworkNamespacePrefixes.Add("MyApp.Orders.OrderHandler");

        theFingerprinter.FindApplicationFrame(StackWithFrameworkFrames)
            .ShouldBe("MyApp.Orders.OrderService.Place()");
    }

    [Fact]
    public void falls_back_to_the_first_frame_when_every_frame_is_framework_code()
    {
        const string stack = """
               at System.Threading.Tasks.Task.Run()
               at Wolverine.Runtime.Executor.Execute()
            """;

        theFingerprinter.FindApplicationFrame(stack).ShouldBe("System.Threading.Tasks.Task.Run()");
    }

    [Fact]
    public void the_exception_message_is_removed_from_the_stack_text_first()
    {
        const string message = "at Customer.42.Data() blew up";
        var stack = $"""
               {message}
               at MyApp.Orders.OrderHandler.Handle(PlaceOrder command) in /src/Orders/OrderHandler.cs:line 22
            """;

        theFingerprinter.FindApplicationFrame(stack, message)
            .ShouldBe("MyApp.Orders.OrderHandler.Handle(PlaceOrder command)");
    }

    [Fact]
    public void no_stack_trace_falls_back_to_the_exception_type()
    {
        var never = fingerprint(new InvalidOperationException("never thrown"));

        never.Title.ShouldBe("System.InvalidOperationException: no stack trace");
        never.Group.ShouldBe(fingerprint(new InvalidOperationException("different text")).Group);
    }

    [Fact]
    public void a_recovered_native_dead_letter_uses_the_original_exception_type()
    {
        var recovered = fingerprint(new DeadLetterRecoveredException("MyApp.PaymentRefusedException", "refused"));

        recovered.Title.ShouldBe("MyApp.PaymentRefusedException: no stack trace");
    }

    [Fact]
    public void stamping_failure_metadata_stamps_the_failure_group()
    {
        var envelope = new Envelope();
        var exception = capture(() => approvePayment("1"));

        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);

        envelope.Headers[DeadLetterQueueConstants.FailureGroupHeader].ShouldBe(fingerprint(exception).Group);
        envelope.Headers[DeadLetterQueueConstants.FailureTitleHeader].ShouldBe(fingerprint(exception).Title);

        DeadLetterQueueConstants.DiagnosticHeaders.ShouldContain(DeadLetterQueueConstants.FailureGroupHeader);
        DeadLetterQueueConstants.DiagnosticHeaders.ShouldContain(DeadLetterQueueConstants.FailureTitleHeader);
    }

    [Fact]
    public void a_group_stamped_where_the_failure_was_caught_survives_a_stackless_exception_later()
    {
        // e.g. the dead letter is recovered from a broker's native dead letter queue, or an
        // IDeadLetterInterceptor substituted a redacted exception
        var envelope = new Envelope();
        var original = capture(() => approvePayment("1"));
        DeadLetterQueueConstants.StampFailureMetadata(envelope, original);

        var applied = DeadLetterFingerprinting.Apply(theFingerprinter, envelope,
            new DeadLetterRecoveredException("System.InvalidOperationException", "redacted"));

        applied.Group.ShouldBe(fingerprint(original).Group);
    }

    [Fact]
    public void a_new_failure_with_a_stack_trace_replaces_an_earlier_group()
    {
        var envelope = new Envelope();
        DeadLetterQueueConstants.StampFailureMetadata(envelope, capture(() => approvePayment("1")));

        var applied = DeadLetterFingerprinting.Apply(theFingerprinter, envelope, capture(() => reserveStock("1")));

        applied.Group.ShouldBe(fingerprint(capture(() => reserveStock("2"))).Group);
        envelope.Headers[DeadLetterQueueConstants.FailureGroupHeader].ShouldBe(applied.Group);
    }

    [Fact]
    public void a_failing_custom_fingerprinter_falls_back_to_the_default()
    {
        var exception = capture(() => approvePayment("1"));

        DeadLetterFingerprinting.Apply(new ThrowingFingerprinter(), new Envelope(), exception).Group
            .ShouldBe(fingerprint(exception).Group);
    }

    [Fact]
    public void long_keys_and_titles_are_truncated_to_fit_the_columns()
    {
        var applied = DeadLetterFingerprinting.Apply(new LongFingerprinter(), new Envelope(), null);

        applied.Group.Length.ShouldBe(DeadLetterFingerprint.MaxGroupLength);
        applied.Title.Length.ShouldBe(DeadLetterFingerprint.MaxTitleLength);
    }

    [Fact]
    public async Task a_fingerprinter_registered_in_the_container_replaces_the_default()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IDeadLetterFingerprinter, ByMessageTypeFingerprinter>();
            }).StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IWolverineRuntime>().DurabilitySettings.DeadLetterFingerprinter
            .ShouldBeOfType<ByMessageTypeFingerprinter>();
    }

    [Fact]
    public void the_default_fingerprinter_is_the_default()
    {
        new DurabilitySettings().DeadLetterFingerprinter.ShouldBeOfType<DefaultDeadLetterFingerprinter>();
        new DurabilitySettings().ArchivedDeadLetterRetention.ShouldBeNull();
    }

    public class ByMessageTypeFingerprinter : IDeadLetterFingerprinter
    {
        public DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception) =>
            new(envelope.MessageType ?? "unknown", $"Failures of {envelope.MessageType}");
    }

    public class ThrowingFingerprinter : IDeadLetterFingerprinter
    {
        public DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception) =>
            throw new DivideByZeroException();
    }

    public class LongFingerprinter : IDeadLetterFingerprinter
    {
        public DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception) =>
            new(new string('g', 1000), new string('t', 1000));
    }
}

public class dead_letter_query_helpers
{
    [Fact]
    public void archived_dead_letters_are_excluded_by_default()
    {
        new DeadLetterEnvelopeQuery().EffectiveArchiveFilter().ShouldBe(DeadLetterArchiveFilter.Exclude);
    }

    [Fact]
    public void explicit_ids_reach_archived_dead_letters_by_default()
    {
        new DeadLetterEnvelopeQuery([Guid.NewGuid()]).EffectiveArchiveFilter().ShouldBe(DeadLetterArchiveFilter.Any);
        new DeadLetterEnvelopeQuery([Guid.NewGuid()]) { Archived = DeadLetterArchiveFilter.Exclude }
            .EffectiveArchiveFilter().ShouldBe(DeadLetterArchiveFilter.Exclude);
    }

    [Fact]
    public void contains_pattern_matches_wildcards_literally()
    {
        DeadLetterSummaries.ContainsPattern("50% off_now [x] !").ShouldBe("%50!% off!_now ![x] !!%");
        DeadLetterSummaries.ContainsPattern("50% [x]", escapeBrackets: false).ShouldBe("%50!% [x]%");
    }

    [Fact]
    public void unfingerprinted_group_keys_round_trip()
    {
        var key = DeadLetterFingerprint.UnfingerprintedGroupFor("System.TimeoutException");

        DeadLetterFingerprint.TryParseUnfingerprintedGroup(key, out var exceptionType).ShouldBeTrue();
        exceptionType.ShouldBe("System.TimeoutException");

        DeadLetterFingerprint.TryParseUnfingerprintedGroup("0123abcd", out _).ShouldBeFalse();
    }

    [Fact]
    public void in_memory_matching()
    {
        var query = new DeadLetterEnvelopeQuery
        {
            ExceptionMessage = "stock", ExceptionMessageMatch = DeadLetterMessageMatch.Contains,
            FailureGroup = DeadLetterFingerprint.UnfingerprintedGroupFor("System.TimeoutException")
        };

        query.MatchesExceptionMessage("out of stock today").ShouldBeTrue();
        query.MatchesExceptionMessage("all good").ShouldBeFalse();

        query.MatchesFailureGroup(null, "System.TimeoutException").ShouldBeTrue();
        query.MatchesFailureGroup("abc", "System.TimeoutException").ShouldBeFalse();
        query.MatchesFailureGroup(null, "System.InvalidOperationException").ShouldBeFalse();

        query.MatchesArchived(null).ShouldBeTrue();
        query.MatchesArchived(DateTimeOffset.UtcNow).ShouldBeFalse();
    }

    [Fact]
    public void operation_results_add_up_only_when_every_part_reports()
    {
        new DeadLetterOperationResult(2).Add(new DeadLetterOperationResult(3)).Affected.ShouldBe(5);
        new DeadLetterOperationResult(2).Add(DeadLetterOperationResult.NotReported).Affected.ShouldBeNull();
    }

    [Fact]
    public void copy_for_paging_keeps_every_criteria()
    {
        var query = new DeadLetterEnvelopeQuery
        {
            MessageType = "a", ExceptionType = "b", ReceivedAt = "c", ExceptionMessage = "d",
            ExceptionMessageMatch = DeadLetterMessageMatch.Exact, FailureGroup = "e", TenantId = "f",
            Archived = DeadLetterArchiveFilter.Only, Replayable = true, Range = TimeRange.AllTime(),
            MessageIds = [Guid.NewGuid()], PageNumber = 4, PageSize = 7
        }.Header("h", "v");

        var copy = query.CopyForPaging(50);

        copy.PageSize.ShouldBe(50);
        copy.PageNumber.ShouldBe(1);
        copy.ShouldBeEquivalentTo(new DeadLetterEnvelopeQuery
        {
            MessageType = "a", ExceptionType = "b", ReceivedAt = "c", ExceptionMessage = "d",
            ExceptionMessageMatch = DeadLetterMessageMatch.Exact, FailureGroup = "e", TenantId = "f",
            Archived = DeadLetterArchiveFilter.Only, Replayable = true, Range = query.Range,
            MessageIds = query.MessageIds, PageNumber = 1, PageSize = 50, QueryId = query.QueryId,
            Headers = new Dictionary<string, string> { ["h"] = "v" }
        });
    }
}
