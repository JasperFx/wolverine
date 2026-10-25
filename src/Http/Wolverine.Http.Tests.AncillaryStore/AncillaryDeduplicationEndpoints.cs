using System.Collections.Concurrent;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Http.Tests.AncillaryStore;

// GH-5205 and GH-5206 on an ANCILLARY Marten store: the claim is written to, and read back from, that store's message
// tables, never the main store's. Here rather than in Wolverine.Http.Tests for the reason the csproj gives.

public record AncillaryCounterStarted;

public record AncillaryCounterIncremented(string Note);

public class AncillaryCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(AncillaryCounterStarted _)
    {
    }

    public void Apply(AncillaryCounterIncremented _) => Count++;
}

public record IncrementAncillaryCounter(Guid AncillaryCounterId, string Note);

public record AncillaryCounterAnswer(int Count);

public record OpenAncillaryAccount(string Name, bool Throws = false);

public record AncillaryAccountOpened(Guid Id);

public class AncillaryAccount
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Holds requests with the same name so that both miss the claim before either commits. <see cref="BothArriveAsync" />
/// lets both go on together; <see cref="FirstGoesFirstAsync" /> lets the first go on once the second is here, and the
/// second only once the test calls <see cref="Release" />. Bounded, so a regression fails the assertions rather than
/// hanging the test.
/// </summary>
public static class AncillaryArrivals
{
    private static readonly ConcurrentDictionary<string, int> _arrivals = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new();

    private static TaskCompletionSource signal(string name)
        => _signals.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    public static int Arrivals(string name) => _arrivals.GetValueOrDefault(name);

    public static void Release(string name) => signal(name + ":released").TrySetResult();

    public static async Task BothArriveAsync(string name)
    {
        if (_arrivals.AddOrUpdate(name, 1, (_, count) => count + 1) == 2) signal(name + ":both").TrySetResult();
        await Task.WhenAny(signal(name + ":both").Task, Task.Delay(Bound));
    }

    public static async Task FirstGoesFirstAsync(string name)
    {
        if (_arrivals.AddOrUpdate(name, 1, (_, count) => count + 1) == 1)
        {
            await Task.WhenAny(signal(name + ":second").Task, Task.Delay(Bound));
            return;
        }

        signal(name + ":second").TrySetResult();
        await Task.WhenAny(signal(name + ":released").Task, Task.Delay(Bound));
    }

    // One account per name
    public static async Task<bool> IsAlreadyOpenAsync(OpenAncillaryAccount command, IDocumentSession session)
    {
        if (!await session.Query<AncillaryAccount>().AnyAsync(x => x.Name == command.Name)) return false;

        // The same rule, failed by a throw rather than an answer
        if (command.Throws) throw new InvalidOperationException("An account with this name is already open");
        return true;
    }
}

public static class AncillaryDeduplicatedResponseAggregateEndpoint
{
    [MartenStore(typeof(INkThingStore))]
    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/ancillary-dedup/response/counter")]
    public static async Task<(AncillaryCounterAnswer, Events)> Post(IncrementAncillaryCounter command,
        [WriteAggregate] AncillaryCounter counter)
    {
        // Both have loaded the same version before either appends
        await AncillaryArrivals.BothArriveAsync(command.Note);
        return (new AncillaryCounterAnswer(counter.Count + 1), [new AncillaryCounterIncremented(command.Note)]);
    }
}

public static class AncillaryDeduplicatedResponseAdmissionEndpoint
{
    public static Task BeforeAsync(OpenAncillaryAccount command) => AncillaryArrivals.FirstGoesFirstAsync(command.Name);

    public static async Task<ProblemDetails> ValidateAsync(OpenAncillaryAccount command, IDocumentSession session)
        => await AncillaryArrivals.IsAlreadyOpenAsync(command, session)
            ? new ProblemDetails { Status = 409, Detail = "An account with this name is already open" }
            : WolverineContinue.NoProblems;

    [MartenStore(typeof(INkThingStore))]
    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/ancillary-dedup/response/accounts")]
    public static AncillaryAccountOpened Post(OpenAncillaryAccount command, IDocumentSession session)
    {
        var account = new AncillaryAccount { Id = Guid.NewGuid(), Name = command.Name };
        session.Store(account);
        return new AncillaryAccountOpened(account.Id);
    }
}

public static class AncillaryDeduplicatedAdmissionEndpoint
{
    public static Task BeforeAsync(OpenAncillaryAccount command) => AncillaryArrivals.FirstGoesFirstAsync(command.Name);

    public static async Task<ProblemDetails> ValidateAsync(OpenAncillaryAccount command, IDocumentSession session)
        => await AncillaryArrivals.IsAlreadyOpenAsync(command, session)
            ? new ProblemDetails { Status = 409, Detail = "An account with this name is already open" }
            : WolverineContinue.NoProblems;

    [MartenStore(typeof(INkThingStore))]
    [Deduplicated]
    [WolverinePost("/ancillary-dedup/accounts")]
    public static string Post(OpenAncillaryAccount command, IDocumentSession session)
    {
        session.Store(new AncillaryAccount { Id = Guid.NewGuid(), Name = command.Name });
        return "ok";
    }
}
