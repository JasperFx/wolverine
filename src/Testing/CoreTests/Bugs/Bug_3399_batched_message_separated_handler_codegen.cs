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
            }).StartAsync();

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
            }).StartAsync();

        await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ItemDeleted3399[]>(host)
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
