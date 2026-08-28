using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

// GH-3399: with MultipleHandlerBehavior.Separated, a handler class that handles two message types where
// one of them is batched (BatchMessagesOf<T>() makes T[] the handled message type) produced duplicate
// HandlerChain.TypeNames -- both sticky chains are named off the *handler type*. The duplicate
// disambiguation in HandlerGraph then rebuilt the generated class name straight off the message type,
// yielding "ItemDeleted[]1177234954_TelemetryHandlerHandler550305999", which is not a valid C#
// identifier -> "Compilation failures!" and the app dies at startup.
public class Bug_3399_batched_message_separated_handler_codegen
{
    [Fact]
    public async Task can_start_up_with_separated_batched_handler()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<TelemetryHandler3399>();
                opts.Discovery.IncludeType<OtherCreatedHandler3399>();
                opts.Discovery.IncludeType<OtherDeletedHandler3399>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.BatchMessagesOf<ItemDeleted3399>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        // Both the batched (array) chain and the single-message chain exist...
        var chains = runtime.Handlers.Chains
            .SelectMany(x => x.ByEndpoint.Any() ? x.ByEndpoint : [x])
            .Where(x => x.Handlers.Any())
            .ToArray();

        // ...and every generated class name is a legal C# identifier. Before the fix, the chain for
        // ItemDeleted3399[] was named "ItemDeleted3399[]<hash>_TelemetryHandler3399Handler<hash>".
        foreach (var chain in chains)
        {
            chain.TypeName.ShouldNotContain("[");
            chain.TypeName.ShouldNotContain("]");
            isValidIdentifier(chain.TypeName).ShouldBeTrue($"'{chain.TypeName}' is not a valid C# identifier");
        }

        // The array chain and the single chain must NOT collide after sanitizing
        chains.Select(x => x.TypeName).Distinct().Count().ShouldBe(chains.Length);
    }

    [Fact]
    public async Task batched_handler_actually_executes()
    {
        TelemetryHandler3399.Batched = 0;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<TelemetryHandler3399>();
                opts.Discovery.IncludeType<OtherCreatedHandler3399>();
                opts.Discovery.IncludeType<OtherDeletedHandler3399>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.BatchMessagesOf<ItemDeleted3399>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Wait for the exact fact this test asserts, and nothing else.
        //
        // Two earlier attempts both raced. WaitForMessageToBeReceivedAt released at receipt, before any
        // handler ran (GH-4167). Its replacement, WaitForExecutionOf<ItemDeleted3399[]>(2), counted
        // ENVELOPES of that type -- and under Separated behavior there are THREE, not two: the batch
        // lands on its own execution queue and is then relayed to each sticky handler queue. Measured:
        //
        //   ExecutionFinished env=...5310 dest=local://coretests.bugs.itemdeleted3399/          <- relay source
        //   ExecutionFinished env=...b332 dest=local://coretests.bugs.otherdeletedhandler3399/  <- sibling
        //   Received         env=...a676 dest=local://coretests.bugs.telemetryhandler3399/      <- never ran
        //
        // Those first two satisfy a count of 2 on their own, so the session was released while the
        // handler under assertion had only been received. It runs a moment later -- probed
        // immediate=0, eventual=1 -- so nothing was ever lost; the test was simply reading the counter
        // too early. Counting envelopes can never express "TelemetryHandler3399 ran", so don't: gate on
        // the counter itself.
        await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForCondition(new TelemetryBatchHandled())
            .SendMessageAndWaitAsync(new ItemDeleted3399(Guid.NewGuid()));

        TelemetryHandler3399.Batched.ShouldBeGreaterThan(0);
    }

    private static bool isValidIdentifier(string name)
    {
        if (name.IsEmpty()) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;

        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

public record ItemDeleted3399(Guid Id);

public record ItemCreated3399(Guid Id);

// The trigger: ONE handler class handling TWO message types, one of which is batched, where each of
// those message types ALSO has a second handler. The second handler is what pushes each message type's
// grouping past 1 (HandlerChain.cs:114), so under MultipleHandlerBehavior.Separated both of
// TelemetryHandler3399's calls get a sticky chain -- and sticky chains are named off the HANDLER type
// (HandlerChain.cs:85). The two sticky chains therefore share a TypeName, which drives HandlerGraph's
// duplicate disambiguation to rebuild the name off the message type: "ItemDeleted3399[]<hash>_...".
/// <summary>
/// Completes the tracked session once <see cref="TelemetryHandler3399"/> has actually handled a batch.
/// Conditions are re-evaluated as envelope records arrive, and the handler increments before its own
/// ExecutionFinished record is written, so this is satisfied no later than that record.
/// </summary>
internal class TelemetryBatchHandled : ITrackedCondition
{
    public void Record(EnvelopeRecord record)
    {
        // Nothing to accumulate -- the handler's own static counter is the observable.
    }

    public bool IsCompleted()
    {
        return TelemetryHandler3399.Batched > 0;
    }
}

[WolverineIgnore]
public class TelemetryHandler3399
{
    public static int Batched;

    public void Handle(ItemCreated3399 created)
    {
    }

    public void Handle(ItemDeleted3399[] deleted)
    {
        Interlocked.Add(ref Batched, deleted.Length);
    }
}

[WolverineIgnore]
public class OtherCreatedHandler3399
{
    public void Handle(ItemCreated3399 created)
    {
    }
}

[WolverineIgnore]
public class OtherDeletedHandler3399
{
    public void Handle(ItemDeleted3399[] deleted)
    {
    }
}
