using System.Text.Json;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

/// <summary>
/// GH-3800. The compliance battery injects errors through <see cref="ErrorCausingMessage"/>, and it
/// used to do so by carrying live <c>Exception</c> instances. <c>System.Text.Json</c> cannot
/// round-trip those, so any transport wired <c>.InteropWithCloudEvents()</c> and run through
/// TransportCompliance silently lost dead-lettering-by-exception-type coverage — the handler threw
/// the wrong type and an exception-match rule could never fire.
///
/// <para>These pin the harness itself rather than a transport. Pulsar is currently the only
/// CloudEvents fixture in the battery, and its DLQ tests are skipped for an unrelated reason
/// (GH-3797), so without these the fix would have no coverage anywhere.</para>
/// </summary>
public class error_injection_survives_serialization_3800
{
    private static ErrorCausingMessage roundTrip(ErrorCausingMessage message)
    {
        // The same serializer CloudEvents uses internally, with no custom converters -- which is
        // exactly the configuration that corrupted the old Dictionary<int, Exception>.
        var json = JsonSerializer.Serialize(message);
        return JsonSerializer.Deserialize<ErrorCausingMessage>(json)!;
    }

    private static void handle(ErrorCausingMessage message, int attempt)
    {
        new ErrorCausingMessageHandler()
            .Handle(message, new Envelope { Attempts = attempt }, new AttemptTracker());
    }

    [Fact]
    public void the_declared_exception_type_survives_a_json_round_trip()
    {
        var message = new ErrorCausingMessage();
        message.ThrowOnAttempt<DivideByZeroException>(1);

        var received = roundTrip(message);

        // Before GH-3800 this threw *something*, but not this -- which is worse than throwing
        // nothing, because an exception-match rule then quietly never fires.
        Should.Throw<DivideByZeroException>(() => handle(received, 1));
    }

    [Fact]
    public void distinct_attempts_keep_their_own_exception_types()
    {
        var message = new ErrorCausingMessage();
        message.ThrowOnAttempt<DivideByZeroException>(1);
        message.ThrowOnAttempt<BadImageFormatException>(2);

        var received = roundTrip(message);

        Should.Throw<DivideByZeroException>(() => handle(received, 1));
        Should.Throw<BadImageFormatException>(() => handle(received, 2));
    }

    [Fact]
    public void an_attempt_with_no_error_is_processed_normally()
    {
        var message = new ErrorCausingMessage();
        message.ThrowOnAttempt<DivideByZeroException>(1);

        var received = roundTrip(message);

        handle(received, 2);

        received.WasProcessed.ShouldBeTrue();
    }

    [Fact]
    public void an_unresolvable_type_name_fails_loudly()
    {
        // A silently-wrong exception type is the failure this replaced, so a name that cannot be
        // resolved in the receiving process must not quietly become "no error".
        var message = new ErrorCausingMessage { Errors = { [1] = "Not.A.Real.Type, Nowhere" } };

        var ex = Should.Throw<InvalidOperationException>(() => handle(roundTrip(message), 1));
        ex.Message.ShouldContain("Not.A.Real.Type");
    }
}
