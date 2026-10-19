namespace Bobcat.CritterStack;

/// <summary>
/// bobcat#497. The wait for the async projections to catch up, before a read such as
/// <see cref="CritterStackScenario.ThenDocument{T}(object)" />, ran out of time. An <b>error</b>, not a specification
/// failure: a daemon that is behind, stopped or not running is the environment, not evidence that the document is
/// wrong. The message names the wait, the projections that were behind (when the store can say), the timeout and
/// what was being read; the store's own exception is the <see cref="Exception.InnerException" />.
/// </summary>
/// <remarks>
/// A <see cref="TimeoutException" />, so code that caught the store's timeout before still catches this one. A wait
/// that reads nothing afterwards (<see cref="CritterStackScenario.AfterAsyncDaemonsCatchUpAsync()" />, a shard-level
/// wait in a suite's own code) has no <see cref="ReadType" /> (bobcat#685). Any suite builds one for its own wait
/// through <see cref="EventStores.DiagnoseCatchUpTimeoutAsync" />.
/// </remarks>
public sealed class ProjectionCatchUpTimeoutException : TimeoutException
{
    public ProjectionCatchUpTimeoutException(string wait, IReadOnlyList<string> behind, TimeSpan timeout, Type? readType,
        object? identity, Exception inner)
        : this(wait, new ProjectionsBehind(behind, null), timeout, readType, identity, inner)
    {
    }

    /// <summary>
    /// bobcat#686. As above, carrying why the store could not say which projections were behind
    /// (<see cref="ProjectionsBehind.Failure" />) into the message and <see cref="BehindFailure" />.
    /// </summary>
    public ProjectionCatchUpTimeoutException(string wait, ProjectionsBehind behind, TimeSpan timeout, Type? readType,
        object? identity, Exception inner)
        : base(describe(wait, behind, timeout, readType, identity, inner), inner)
    {
        Wait = wait;
        Behind = behind.Shards;
        BehindFailure = behind.Failure;
        Timeout = timeout;
        ReadType = readType;
        Identity = identity;
    }

    /// <summary>The wait that timed out, as the step that ran it reads.</summary>
    public string Wait { get; }

    /// <summary>
    /// The projection shards that had not caught up, as <c>'shard' at N of M</c>; empty when the store could not say.
    /// N is the shard's progress and M the highest event <b>sequence</b> number, not a count of events: deleting
    /// event data does not reset the sequence, so a few events after a wipe can read as <c>at 0 of 461</c>.
    /// </summary>
    public IReadOnlyList<string> Behind { get; }

    /// <summary>
    /// bobcat#686. The exception that stopped the store saying which projections were behind (a schema another
    /// process dropped, say), or null when it said all it could.
    /// </summary>
    public Exception? BehindFailure { get; }

    /// <summary>How long the wait was given (<see cref="CritterStackScenario.ProjectionTimeout" />).</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The type the read after the wait was for, or null for a wait that reads nothing afterwards.</summary>
    public Type? ReadType { get; }

    /// <summary>The identity being read, or null for a read with none (every document of a type, the scenario's stream).</summary>
    public object? Identity { get; }

    private static string describe(string wait, ProjectionsBehind behind, TimeSpan timeout, Type? readType, object? identity,
        Exception inner)
    {
        var reading = readType is null
            ? ""
            : identity is null ? $", before reading the {readType.Name}" : $", before reading the {readType.Name} with id {identity}";
        var about = readType is null ? "any read model" : $"the {readType.Name} itself";
        var why = behind.Failure is { } failure ? $" ({failure.GetType().Name}: {failure.Message})" : "";
        var shards = (behind.Shards.Count, behind.Failure) switch
        {
            (0, null) => "The store could not say which projection was behind.",
            (0, _) => $"The store could not say which projection was behind{why}.",
            (_, null) => $"Behind: {string.Join(", ", behind.Shards)}.",
            _ => $"Behind: {string.Join(", ", behind.Shards)}; the rest could not be read{why}."
        };
        return $"Waiting for '{wait}' timed out after {timeout.TotalSeconds:0.###}s{reading}. {shards} "
               + $"This is the environment (a daemon behind, stopped or not running), not evidence about {about}. "
               + $"The store said: {inner.GetType().Name}: {inner.Message}";
    }
}
