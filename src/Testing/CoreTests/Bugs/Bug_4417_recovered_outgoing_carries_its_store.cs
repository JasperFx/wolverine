using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4417.
///
/// <para>
/// <see cref="Envelope.Store" /> is an in-memory-only reference: it does not survive the trip
/// through persistence, so an envelope read back by <c>LoadOutgoingAsync</c> has none.
/// <c>DelegatingMessageOutbox</c> routes every acknowledgement on that property and falls back to
/// the <em>main</em> store when it is null.
/// </para>
///
/// <para>
/// For an envelope recovered from an <see cref="MessageStoreRole.Ancillary" /> store that is the
/// whole bug: the delete is issued against the main store's outgoing table, matches nothing, and
/// the ancillary row survives — so the message is recovered, sent and handled again on every
/// restart, forever. The reporter saw several hundred model-calling messages replayed per restart.
/// </para>
///
/// <para>
/// <c>RecoverIncomingMessagesCommand</c> has stamped the store on recovered <em>incoming</em>
/// envelopes since GH-2318 for precisely this reason. This pins down the outgoing twin.
/// </para>
/// </summary>
public class Bug_4417_recovered_outgoing_carries_its_store : IAsyncLifetime
{
    private static readonly Uri TheDestination = "stub://ancillary-outbound".ToUri();

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<AncillaryOutboxMessage>().To(TheDestination);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task recovered_outgoing_envelope_carries_the_store_it_was_loaded_from()
    {
        var runtime = _host.GetRuntime();
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(TheDestination);

        // An outgoing envelope as it comes back out of storage: no in-memory Store reference,
        // because that is not a persisted column.
        var recovered = new Envelope(new AncillaryOutboxMessage("recover me"))
        {
            Destination = TheDestination,
            Serializer = runtime.Options.DefaultSerializer,
            ContentType = runtime.Options.DefaultSerializer!.ContentType
        };

        recovered.Store.ShouldBeNull(
            "Precondition: the originating store does not survive persistence");

        // Stand in for the ANCILLARY store this envelope was loaded from. The bug is not that
        // this store is wrong, it is that the recovered envelope never points back at it.
        var ancillaryStore = Substitute.For<IMessageStore>();
        var ancillaryOutbox = Substitute.For<IMessageOutbox>();
        ancillaryStore.Outbox.Returns(ancillaryOutbox);
        ancillaryOutbox.LoadOutgoingAsync(TheDestination)
            .Returns(new List<Envelope> { recovered });
        ancillaryOutbox
            .DiscardAndReassignOutgoingAsync(Arg.Any<Envelope[]>(), Arg.Any<Envelope[]>(), Arg.Any<int>())
            .Returns(Task.CompletedTask);

        var command = new RecoverOutgoingMessagesCommand(sendingAgent, ancillaryStore, NullLogger.Instance);
        await command.ExecuteAsync(runtime, CancellationToken.None);

        recovered.Store.ShouldBeSameAs(ancillaryStore);
    }

    [Fact]
    public async Task an_already_stamped_store_is_left_alone()
    {
        var runtime = _host.GetRuntime();
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(TheDestination);

        // The stamp is ??=, not =, so a caller that already knows the right store keeps it. This
        // matters for the same reason it does on WireTap: recovery must not overwrite a reference
        // someone else established.
        var alreadyKnown = Substitute.For<IMessageStore>();

        var recovered = new Envelope(new AncillaryOutboxMessage("leave me alone"))
        {
            Destination = TheDestination,
            Serializer = runtime.Options.DefaultSerializer,
            ContentType = runtime.Options.DefaultSerializer!.ContentType,
            Store = alreadyKnown
        };

        var otherStore = Substitute.For<IMessageStore>();
        var otherOutbox = Substitute.For<IMessageOutbox>();
        otherStore.Outbox.Returns(otherOutbox);
        otherOutbox.LoadOutgoingAsync(TheDestination)
            .Returns(new List<Envelope> { recovered });
        otherOutbox
            .DiscardAndReassignOutgoingAsync(Arg.Any<Envelope[]>(), Arg.Any<Envelope[]>(), Arg.Any<int>())
            .Returns(Task.CompletedTask);

        var command = new RecoverOutgoingMessagesCommand(sendingAgent, otherStore, NullLogger.Instance);
        await command.ExecuteAsync(runtime, CancellationToken.None);

        recovered.Store.ShouldBeSameAs(alreadyKnown);
    }
}

public record AncillaryOutboxMessage(string Name);
