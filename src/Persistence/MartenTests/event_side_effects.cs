using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests;

// Storage.AppendEvents() / Storage.StartStream() are store agnostic side effects expressed purely against
// JasperFx.Events' IEventOperations. This is the Marten proof; PolecatTests and FisherTests run the same
// handlers against their own stores.
public class event_side_effects : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InvoiceHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "event_side_effects";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task start_stream_creates_the_stream_and_its_events()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        await using var session = _host.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<InvoiceCreated>().Amount.ShouldBe(100);
    }

    [Fact]
    public async Task append_events_adds_to_an_existing_stream()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        await _host.InvokeMessageAndWaitAsync(new ApproveInvoice(id, "kareem"));

        await using var session = _host.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<InvoiceApproved>().ApprovedBy.ShouldBe("kareem");
    }

    [Fact]
    public async Task append_several_events_at_once_keeps_their_order()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        await _host.InvokeMessageAndWaitAsync(new CloseInvoice(id));

        await using var session = _host.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(3);
        events[1].Data.ShouldBeOfType<InvoiceApproved>();
        events[2].Data.ShouldBeOfType<InvoiceClosed>();
    }

    [Fact]
    public async Task an_empty_append_is_a_no_op_rather_than_an_empty_stream_action()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        // A decision function that concludes "nothing to do" is a legitimate outcome, and must not
        // hand the store a stream action carrying no events
        await _host.InvokeMessageAndWaitAsync(new MaybeApproveInvoice(id, false));

        await using var session = _host.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task works_as_part_of_a_tuple_return()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        var tracked = await _host.InvokeMessageAndWaitAsync(new ApproveInvoiceAndNotify(id, "sabonis"));

        // the cascaded message went out...
        tracked.Sent.SingleMessage<InvoiceApprovalNoticed>().InvoiceId.ShouldBe(id);

        // ...and the side effect in the same tuple still appended
        await using var session = _host.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<InvoiceApproved>().ApprovedBy.ShouldBe("sabonis");
    }
}

public record InvoiceCreated(decimal Amount);

public record InvoiceApproved(string ApprovedBy);

public record InvoiceClosed;

public record CreateInvoice(Guid Id, decimal Amount);

public record ApproveInvoice(Guid Id, string ApprovedBy);

public record CloseInvoice(Guid Id);

public record MaybeApproveInvoice(Guid Id, bool Approve);

public record ApproveInvoiceAndNotify(Guid Id, string ApprovedBy);

public record InvoiceApprovalNoticed(Guid InvoiceId);

public static class InvoiceHandler
{
    // No IDocumentSession anywhere in this class -- that is the whole point
    public static StartStream Handle(CreateInvoice command)
        => Storage.StartStream(command.Id, new InvoiceCreated(command.Amount));

    public static AppendEvents Handle(ApproveInvoice command)
        => Storage.AppendEvents(command.Id, new InvoiceApproved(command.ApprovedBy));

    public static AppendEvents Handle(CloseInvoice command)
        => Storage.AppendEvents(command.Id, new InvoiceApproved("auto"), new InvoiceClosed());

    public static AppendEvents Handle(MaybeApproveInvoice command)
        => command.Approve
            ? Storage.AppendEvents(command.Id, new InvoiceApproved("maybe"))
            : Storage.AppendEvents(command.Id);

    public static (AppendEvents, InvoiceApprovalNoticed) Handle(ApproveInvoiceAndNotify command)
        => (Storage.AppendEvents(command.Id, new InvoiceApproved(command.ApprovedBy)),
            new InvoiceApprovalNoticed(command.Id));

    public static void Handle(InvoiceApprovalNoticed msg)
    {
    }
}
