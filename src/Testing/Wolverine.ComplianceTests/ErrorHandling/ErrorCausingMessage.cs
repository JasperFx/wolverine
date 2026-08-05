namespace Wolverine.ComplianceTests.ErrorHandling;

public class ErrorCausingMessage
{
    /// <summary>
    /// Identifies one specific message under test. The transport compliance suites share broker
    /// queues across test methods, and a redelivery still in flight when one test's tracked session
    /// completes will show up in the *next* test's session. Assertions have to scope to this id
    /// rather than to the message type, or a straggler becomes "the ending activity."
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Which attempt should throw what, keyed by attempt number and carrying the exception's
    /// assembly-qualified type NAME rather than a live Exception instance.
    ///
    /// <para>GH-3800. This used to be a <c>Dictionary&lt;int, Exception&gt;</c>, which only works
    /// for serializers that can carry an arbitrary exception graph. <c>System.Text.Json</c> cannot:
    /// under CloudEvents the dictionary arrived corrupted, the handler threw the wrong type, and an
    /// exception-match rule could never fire — so <c>with_cloud_events</c> opted out of
    /// <c>will_move_to_dead_letter_queue_with_exception_match</c> entirely. The hole was in this
    /// shared harness, not in one transport: any transport wired <c>.InteropWithCloudEvents()</c>
    /// and run through TransportCompliance inherited it.</para>
    ///
    /// <para>A type name is a string, so it survives every serializer we run the battery under. The
    /// handler rehydrates it — see <see cref="ErrorCausingMessageHandler"/>.</para>
    /// </summary>
    public Dictionary<int, string> Errors { get; set; } = new();

    public bool WasProcessed { get; set; }
    public int LastAttempt { get; set; }

    /// <summary>
    /// Records that <paramref name="attempt"/> should throw <typeparamref name="TException"/>.
    /// Kept here rather than at the call sites so the name/instance distinction lives in one place.
    /// </summary>
    public void ThrowOnAttempt<TException>(int attempt) where TException : Exception, new()
    {
        Errors[attempt] = typeof(TException).AssemblyQualifiedName!;
    }
}
