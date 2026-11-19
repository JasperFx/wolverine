using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-5673: the rules of the undo stack, on in-memory sagas with local queues. The trip booking and money transfer
// stories are told by the SagaWorkflows specifications
public class saga_undo_stack_5673
{
    private static async Task<IHost> hostAsync(params Type[] types)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var type in types) discovery.IncludeType(type);
                opts.Services.AddSingleton<Kitchen>();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static PartySaga saga(IHost host, Guid id)
        => host.Services.GetRequiredService<InMemorySagaPersistor>().Load<PartySaga>(id)!;

    private static Task<ITrackedSession> sendAsync(IHost host, object message)
        => host.TrackActivity().DoNotAssertOnExceptionsDetected().Timeout(15.Seconds()).SendMessageAndWaitAsync(message);

    [Fact]
    public async Task an_entry_pushed_with_a_step_is_pending_until_the_steps_reply()
    {
        using var host = await hostAsync(typeof(PartySaga), typeof(PartyCaterer));
        var kitchen = host.Services.GetRequiredService<Kitchen>();
        kitchen.Hold = true;
        var id = Guid.NewGuid();

        await sendAsync(host, new PlanParty(id));

        // OrderCake is sent and not answered yet
        saga(host, id).UndoStack.Single().Pending.ShouldBeTrue();

        await sendAsync(host, new CakeOrdered(id) );
        var entry = saga(host, id).UndoStack.Single();
        entry.Step.ShouldBe("cake");
        entry.PushedBy.ShouldBe(typeof(PlanParty).FullName);
    }

    [Fact]
    public async Task the_steps_reply_confirms_the_entry()
    {
        using var host = await hostAsync(typeof(PartySaga), typeof(PartyCaterer));
        var id = Guid.NewGuid();

        await sendAsync(host, new PlanParty(id));

        saga(host, id).UndoStack.Single().Pending.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfirmUndo_confirms_by_step()
    {
        using var host = await hostAsync(typeof(PartySaga), typeof(PartyCaterer));
        host.Services.GetRequiredService<Kitchen>().Hold = true;
        var id = Guid.NewGuid();

        await sendAsync(host, new PlanParty(id));
        await sendAsync(host, new CakeConfirmedByPhone(id));

        saga(host, id).UndoStack.Single().Pending.ShouldBeFalse();
    }

    [Fact]
    public async Task an_unwind_without_pending_entries_skips_the_unconfirmed_step()
    {
        using var host = await hostAsync(typeof(PartySaga), typeof(PartyCaterer));
        host.Services.GetRequiredService<Kitchen>().Hold = true;
        var id = Guid.NewGuid();

        await sendAsync(host, new PlanParty(id));
        var session = await sendAsync(host, new CancelPartyCarefully(id));

        session.Sent.MessagesOf<CancelCake>().ShouldBeEmpty();
        saga(host, id).UndoStack.Single().Step.ShouldBe("cake");
    }

    [Fact]
    public async Task a_sequential_unwind_is_confirmed_by_acknowledgement()
    {
        using var host = await hostAsync(typeof(PartySaga), typeof(PartyCaterer));
        var id = Guid.NewGuid();

        await sendAsync(host, new PlanParty(id));
        await sendAsync(host, new BookBand(id));

        var session = await sendAsync(host, new FindVenue(id));

        session.Executed.MessagesOf<CancelBand>().Count().ShouldBe(1);
        session.Executed.MessagesOf<CancelCake>().Count().ShouldBe(1);

        var party = saga(host, id);
        party.Status.ShouldBe(SagaStatus.Compensated);
        party.UndoStack.ShouldBeEmpty();
        party.CompensationLog.Single().Undone!.Select(x => x.Step).ShouldBe(["band", "cake"]);
    }
}

public class Kitchen
{
    public bool Hold { get; set; }
}

public record PlanParty(Guid PartySagaId);

public record OrderCake(Guid PartySagaId);

public record CakeOrdered(Guid PartySagaId);

public record CakeConfirmedByPhone(Guid PartySagaId);

public record CancelCake(Guid PartySagaId);

public record BookBand(Guid PartySagaId);

public record CancelBand(Guid PartySagaId);

public record FindVenue(Guid PartySagaId);

public record BookVenue(Guid PartySagaId);

public record CancelPartyCarefully(Guid PartySagaId);

[TerminalFailure]
public class VenueTakenException() : Exception("The venue is taken");

[WolverineIgnore]
public class PartyCaterer
{
    public static CakeOrdered? Handle(OrderCake command, Kitchen kitchen) => kitchen.Hold ? null : new CakeOrdered(command.PartySagaId);

    public static void Handle(CancelCake command) { }

    public static void Handle(CancelBand command) { }

    public static void Handle(BookVenue command) => throw new VenueTakenException();
}

[WolverineIgnore]
public class PartySaga : CompensatingSaga
{
    public Guid Id { get; set; }

    public static (PartySaga, OrderCake) Start(PlanParty command)
    {
        var party = new PartySaga { Id = command.PartySagaId };
        party.PushUndo(new CancelCake(command.PartySagaId), "cake");
        return (party, new OrderCake(command.PartySagaId));
    }

    public void Handle(CakeOrdered ordered) { }

    public void Handle(CakeConfirmedByPhone confirmed) => ConfirmUndo("cake");

    // Pushed after the fact: nothing else is sent, so the entry is confirmed at once
    public void Handle(BookBand band) => PushUndo(new CancelBand(Id), "band");

    public OutgoingMessages Handle(CancelPartyCarefully cancel) => UndoMessages(includePending: false);

    public BookVenue Handle(FindVenue find) => new(Id);

    public UndoSequence Compensate(BookVenue failed, RemoteFailure<VenueTakenException> ex) => UndoSequentially();
}
