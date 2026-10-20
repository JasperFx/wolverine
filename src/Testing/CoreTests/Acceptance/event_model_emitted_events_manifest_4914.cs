using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Events;
using Wolverine.Attributes;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Xunit;

// Exactly what JasperFx.Events.SourceGenerator writes for these handlers' bodies (jasperfx#990).
// CoreTests does not run the generator, so the manifest is written here by hand: this tests the READER.
[assembly: EmittedEvents(typeof(CoreTests.Acceptance.EventModel4914.OpenAccountHandler), "Handle",
    typeof(CoreTests.Acceptance.EventModel4914.AccountOpened))]
[assembly: EmittedEvents(typeof(CoreTests.Acceptance.EventModel4914.DepositHandler), "Handle",
    typeof(CoreTests.Acceptance.EventModel4914.FundsDeposited), typeof(CoreTests.Acceptance.EventModel4914.BalanceChecked))]

// GH-5089 / jasperfx#1021: one entry per overload, told apart by ParameterTypes, plus the name-only entry
// the generator falls back to for an overload whose parameter types it cannot name from assembly scope
[assembly: EmittedEvents(typeof(CoreTests.Acceptance.EventModel4914.ClaimHandler), "Handle",
    typeof(CoreTests.Acceptance.EventModel4914.NodeClaimed), typeof(CoreTests.Acceptance.EventModel4914.ClaimRenewed),
    ParameterTypes = new[] { typeof(CoreTests.Acceptance.EventModel4914.ClaimNode), typeof(IEventStream<CoreTests.Acceptance.EventModel4914.Claim>) })]
[assembly: EmittedEvents(typeof(CoreTests.Acceptance.EventModel4914.ClaimHandler), "Handle",
    typeof(CoreTests.Acceptance.EventModel4914.ClaimReleased),
    ParameterTypes = new[] { typeof(CoreTests.Acceptance.EventModel4914.ReleaseNode), typeof(IEventStream<CoreTests.Acceptance.EventModel4914.Claim>) })]
[assembly: EmittedEvents(typeof(CoreTests.Acceptance.EventModel4914.ClaimHandler), "Handle",
    typeof(CoreTests.Acceptance.EventModel4914.QuestionAsked))]

namespace CoreTests.Acceptance.EventModel4914;

// GH-4914: the events a handler's body constructs are on the derived slice with no [Emits]
public class emitted_events_from_the_source_generators_manifest_4914
{
    // The scaffold writes static handler classes, so the chain is built from the type, not a generic argument
    private static HandlerChain chainFor(Type handlerType) => HandlerChain.For(handlerType, "Handle", new HandlerGraph());

    [Fact]
    public void the_manifest_names_the_events_a_start_stream_carries()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor(typeof(OpenAccountHandler)));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(AccountOpened) });
    }

    [Fact]
    public void the_manifest_is_unioned_with_emits_and_never_duplicates_an_event()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor(typeof(DepositHandler)));

        // [Emits] first, then the manifest; FundsDeposited is in both and listed once
        slice.EmittedEvents.Select(x => x.Name)
            .ShouldBe(new[] { nameof(FundsDeposited), nameof(OverdraftCleared), nameof(BalanceChecked) }, ignoreOrder: true);
        slice.EmittedEvents.Count.ShouldBe(3);
    }

    [Fact]
    public void a_method_the_manifest_does_not_name_is_unchanged()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor(typeof(CloseAccountHandler)));

        slice.EmittedEvents.ShouldBeEmpty();
    }

    // HandlerChain.For(Type, string) cannot pick one overload out of several, so the chain is built from the
    // overload's own MethodInfo
    private static HandlerChain chainForOverload(Type messageType)
    {
        var method = typeof(ClaimHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(x => x.Name == "Handle" && x.GetParameters()[0].ParameterType == messageType);

        return new HandlerChain(new MethodCall(typeof(ClaimHandler), method), new HandlerGraph(), []);
    }

    [Theory]
    [InlineData(typeof(ClaimNode), new[] { nameof(NodeClaimed), nameof(ClaimRenewed) })]
    [InlineData(typeof(ReleaseNode), new[] { nameof(ClaimReleased) })]
    public void each_overload_gets_only_the_events_its_own_entry_lists_5089(Type messageType, string[] expected)
    {
        var slice = EventModelRoles.ForHandlerChain(chainForOverload(messageType));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void an_overload_with_no_entry_of_its_own_falls_back_to_the_name_only_entries_5089()
    {
        // The generator writes a name-only entry for an overload it could not describe; the overloads that
        // have their own entry never see it
        var slice = EventModelRoles.ForHandlerChain(chainForOverload(typeof(AskPerson)));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(QuestionAsked) });
    }
}

public record ClaimNode(Guid Id);
public record ReleaseNode(Guid Id);
public record AskPerson(Guid Id);

public record NodeClaimed;
public record ClaimRenewed;
public record ClaimReleased;
public record QuestionAsked;

public class Claim
{
    public Guid Id { get; set; }
}

// Stoat's shape (jasperfx#1021): one handler class with a Handle overload per command
[WolverineIgnore]
public static class ClaimHandler
{
    public static void Handle(ClaimNode command, IEventStream<Claim> stream)
    {
        stream.AppendOne(new NodeClaimed());
        stream.AppendOne(new ClaimRenewed());
    }

    public static void Handle(ReleaseNode command, IEventStream<Claim> stream) => stream.AppendOne(new ClaimReleased());

    public static void Handle(AskPerson command, IEventStream<Claim> stream) => stream.AppendOne(new QuestionAsked());
}

public record OpenAccount(Guid Id);
public record Deposit(Guid Id, decimal Amount);
public record CloseAccount(Guid Id);

public record AccountOpened(Guid Id);
public record FundsDeposited(decimal Amount);
public record OverdraftCleared;
public record BalanceChecked;

public class Account
{
    public Guid Id { get; set; }
}

// Never discovered: it would reach every CoreTests host, none of which has an event store
[WolverineIgnore]
public static class OpenAccountHandler
{
    public static StartStream Handle(OpenAccount command) => Storage.StartStream<Account>(command.Id, new AccountOpened(command.Id));
}

[WolverineIgnore]
public static class DepositHandler
{
    [Emits(typeof(FundsDeposited), typeof(OverdraftCleared))]
    public static EventsToAppend Handle(Deposit command, [WriteModel] Account account)
        => new() { new FundsDeposited(command.Amount), new BalanceChecked() };
}

[WolverineIgnore]
public static class CloseAccountHandler
{
    public static StartStream Handle(CloseAccount command) => Storage.StartStream<Account>(command.Id);
}
