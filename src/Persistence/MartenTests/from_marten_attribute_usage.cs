using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
///     <c>[FromMarten]</c> is <c>[Entity]</c> that always resolves through Marten. It inherits
///     <c>EntityAttribute.Modify</c> outright and overrides only the provider selection, so this suite does NOT
///     re-run the whole <c>[Entity]</c> matrix — see <see cref="missing_data_handling_with_entity_attributes" /> for
///     that. It proves the inheritance is real by exercising one representative of each inherited behavior, and then
///     covers what is genuinely new: the two ways naming a provider explicitly can fail.
/// </summary>
public class from_marten_attribute_usage : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TicketHandler))
                    .IncludeType(typeof(InspectTicketHandler));

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "from_marten";
                    m.Schema.For<Ticket>().SoftDeleted();
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _host.DocumentStore().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Ticket));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Ticket> storeTicket(Action<Ticket>? configure = null)
    {
        var ticket = new Ticket { Subject = "printer on fire" };
        configure?.Invoke(ticket);

        await using var session = _host.DocumentStore().LightweightSession();
        session.Store(ticket);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return ticket;
    }

    [Fact]
    public async Task loads_the_document_by_the_conventional_id_member()
    {
        var ticket = await storeTicket();

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadTicket(ticket.Id));

        tracked.Sent.SingleMessage<TicketRead>().Subject.ShouldBe("printer on fire");
    }

    [Fact]
    public async Task loads_the_document_by_the_type_name_id_convention()
    {
        var ticket = await storeTicket();

        var tracked = await _host.InvokeMessageAndWaitAsync(new EscalateTicket(ticket.Id));

        tracked.Sent.SingleMessage<TicketRead>().Subject.ShouldBe("printer on fire");
    }

    [Fact]
    public async Task loads_the_document_by_an_explicitly_named_argument()
    {
        var ticket = await storeTicket();

        var tracked = await _host.InvokeMessageAndWaitAsync(new AuditTicket(ticket.Id));

        tracked.Sent.SingleMessage<TicketRead>().Subject.ShouldBe("printer on fire");
    }

    [Fact]
    public async Task required_and_missing_stops_the_handler()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadTicket(Guid.NewGuid()));

        tracked.Sent.AllMessages().Any().ShouldBeFalse();
    }

    [Fact]
    public async Task on_missing_throw_exception_with_a_custom_message()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new DemandTicket(Guid.NewGuid()));
        });

        ex.Message.ShouldContain("No ticket like that");
    }

    [Fact]
    public async Task on_missing_empty_content_with_204_just_stops_in_a_handler()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new PeekAtTicket(Guid.NewGuid()));

        tracked.Sent.AllMessages().Any().ShouldBeFalse();
    }

    [Fact]
    public async Task not_required_hands_the_handler_a_null()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new MaybeReadTicket(Guid.NewGuid()));

        tracked.Sent.SingleMessage<TicketRead>().Subject.ShouldBe("(none)");
    }

    [Fact]
    public async Task maybe_soft_deleted_false_treats_a_deleted_document_as_missing()
    {
        var ticket = await storeTicket();

        await using (var session = _host.DocumentStore().LightweightSession())
        {
            session.Delete(ticket);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The default MaybeSoftDeleted = true still hands the handler the document...
        var stillVisible = await _host.InvokeMessageAndWaitAsync(new ReadTicket(ticket.Id));
        stillVisible.Sent.SingleMessage<TicketRead>().Subject.ShouldBe("printer on fire");

        // ...while MaybeSoftDeleted = false nulls it out, and Required = true then stops the handler
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadLiveTicket(ticket.Id));
        tracked.Sent.AllMessages().Any().ShouldBeFalse();
    }

    [Fact]
    public async Task the_entity_is_available_to_a_before_method()
    {
        var open = await storeTicket();
        var closed = await storeTicket(x => x.Closed = true);

        InspectTicketHandler.Inspected.Clear();

        await _host.InvokeAsync(new InspectTicket(open.Id));
        await _host.InvokeAsync(new InspectTicket(closed.Id));

        // The Before method got the same loaded document the handler would have, and used it to stop
        // the closed one short of the handler
        InspectTicketHandler.Inspected.ShouldBe([open.Id]);
    }

    [Fact]
    public void the_load_really_does_go_through_marten()
    {
        // Forces the chain to compile so SourceCode is populated
        _host.GetRuntime().Handlers.HandlerFor<ReadTicket>();

        var chain = _host.GetRuntime().Handlers.ChainFor<ReadTicket>();
        chain.ShouldNotBeNull();

        var code = chain.SourceCode;
        code.ShouldNotBeNull();

        // The Marten frame provider's own session and its LoadAsync -- not a generic "some provider
        // claimed it" load
        code.ShouldContain("Wolverine.Marten.Publishing.OutboxedSessionFactory");
        code.ShouldContain("documentSession.LoadAsync<MartenTests.Ticket>");
    }
}

/// <summary>
///     The first of the two failure modes that only an explicit attribute can have: the store it names is not part
///     of the application at all. A plain <c>[Entity]</c> would have fallen through to whatever else was registered
///     — or to the generic "could not determine a matching persistence service" — and neither says the useful thing.
/// </summary>
public class from_marten_without_marten_registered
{
    [Fact]
    public async Task names_the_parameter_the_tool_and_the_bootstrapping_call()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NoMartenHandler));
                }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

            host.GetRuntime().Handlers.HandlerFor<ReadTicketWithoutMarten>();
        });

        ex.Message.ShouldContain("[FromMarten]");
        ex.Message.ShouldContain("'ticket'");
        ex.Message.ShouldContain(nameof(NoMartenHandler));
        ex.Message.ShouldContain("Marten is not integrated with this Wolverine application");
        ex.Message.ShouldContain("IntegrateWithWolverine()");
    }
}

public class Ticket
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = null!;
    public bool Closed { get; set; }
}

public record ReadTicket(Guid Id);

public record EscalateTicket(Guid TicketId);

public record AuditTicket(Guid Reference);

public record DemandTicket(Guid Id);

public record PeekAtTicket(Guid Id);

public record MaybeReadTicket(Guid Id);

public record ReadLiveTicket(Guid Id);

public record TicketRead(string Subject);

public static class TicketHandler
{
    public static TicketRead Handle(ReadTicket command, [FromMarten] Ticket ticket)
        => new(ticket.Subject);

    public static TicketRead Handle(EscalateTicket command, [FromMarten] Ticket ticket)
        => new(ticket.Subject);

    public static TicketRead Handle(AuditTicket command, [FromMarten(nameof(AuditTicket.Reference))] Ticket ticket)
        => new(ticket.Subject);

    public static TicketRead Handle(DemandTicket command,
        [FromMarten(OnMissing = OnMissing.ThrowException, MissingMessage = "No ticket like that")] Ticket ticket)
        => new(ticket.Subject);

    public static TicketRead Handle(PeekAtTicket command,
        [FromMarten(OnMissing = OnMissing.EmptyContentWith204)] Ticket ticket)
        => new(ticket.Subject);

    public static TicketRead Handle(MaybeReadTicket command, [FromMarten(Required = false)] Ticket? ticket)
        => new(ticket?.Subject ?? "(none)");

    public static TicketRead Handle(ReadLiveTicket command, [FromMarten(MaybeSoftDeleted = false)] Ticket ticket)
        => new(ticket.Subject);

    public static void Handle(TicketRead read)
    {
    }
}

public record InspectTicket(Guid Id);

public static class InspectTicketHandler
{
    public static List<Guid> Inspected { get; } = [];

    public static HandlerContinuation Before([FromMarten] Ticket ticket)
        => ticket.Closed ? HandlerContinuation.Stop : HandlerContinuation.Continue;

    public static void Handle(InspectTicket command, Ticket ticket) => Inspected.Add(ticket.Id);
}

public record ReadTicketWithoutMarten(Guid Id);

public static class NoMartenHandler
{
    public static void Handle(ReadTicketWithoutMarten command, [FromMarten] Ticket ticket)
    {
    }
}
