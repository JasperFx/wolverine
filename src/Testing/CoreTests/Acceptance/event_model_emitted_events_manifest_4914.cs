using JasperFx.Events;
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

namespace CoreTests.Acceptance.EventModel4914;

// GH-4914: the events a handler's body constructs are on the derived slice with no [Emits]
public class emitted_events_from_the_source_generators_manifest_4914
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void the_manifest_names_the_events_a_start_stream_carries()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<OpenAccountHandler>(x => OpenAccountHandler.Handle(null!)));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(AccountOpened) });
    }

    [Fact]
    public void the_manifest_is_unioned_with_emits_and_never_duplicates_an_event()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<DepositHandler>(x => DepositHandler.Handle(null!, null!)));

        // [Emits] first, then the manifest; FundsDeposited is in both and listed once
        slice.EmittedEvents.Select(x => x.Name)
            .ShouldBe(new[] { nameof(FundsDeposited), nameof(OverdraftCleared), nameof(BalanceChecked) }, ignoreOrder: true);
        slice.EmittedEvents.Count.ShouldBe(3);
    }

    [Fact]
    public void a_method_the_manifest_does_not_name_is_unchanged()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<CloseAccountHandler>(x => CloseAccountHandler.Handle(null!)));

        slice.EmittedEvents.ShouldBeEmpty();
    }
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

public class OpenAccountHandler
{
    public static StartStream Handle(OpenAccount command) => Storage.StartStream<Account>(command.Id, new AccountOpened(command.Id));
}

public class DepositHandler
{
    [Emits(typeof(FundsDeposited), typeof(OverdraftCleared))]
    public static EventsToAppend Handle(Deposit command, [WriteModel] Account account)
        => new() { new FundsDeposited(command.Amount), new BalanceChecked() };
}

public class CloseAccountHandler
{
    public static StartStream Handle(CloseAccount command) => Storage.StartStream<Account>(command.Id);
}
