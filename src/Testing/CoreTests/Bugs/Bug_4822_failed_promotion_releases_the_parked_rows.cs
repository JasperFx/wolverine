using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Wolverine.Tracking;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// GH-4822. When handing a group of promoted scheduled envelopes onward fails, <c>EnqueueDirectlyAsync</c>
/// releases the rows back to any node so that recovery retries them. Two things have to be right about WHICH
/// rows and WHERE:
///
/// <para>
/// Only the envelopes that were not handed over yet. One that already left for the slot has had its inbox row
/// retired; releasing it as well would let recovery run it a second time.
/// </para>
///
/// <para>
/// At the address the row was parked under. The slot forward re-addresses each envelope to the slot before
/// sending (GH-4700), but the release matches on id AND <c>received_at</c>, so naming the live destination
/// would match nothing and leave the row owned by this live node -- the very stranding the release exists to
/// prevent.
/// </para>
///
/// <para>
/// The failing send is a substituted sending agent registered for the slot; the stub transport itself never
/// fails a send.
/// </para>
/// </summary>
public class Bug_4822_failed_promotion_releases_the_parked_rows : IAsyncLifetime
{
    private static readonly Uri TheSlot = "stub://partition-slot-4822".ToUri();
    private static readonly Uri TheCompanionQueue = "local://global-partition-slot-4822".ToUri();

    private IHost _host = null!;
    private ISendingAgent _slotSender = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PartitionedRetryMessageHandler>();
                opts.PublishMessage<PartitionedRetryMessage>().To(TheSlot);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        runtime.Endpoints.EndpointFor(TheSlot)!.GlobalPartitionLocalQueueUri = TheCompanionQueue;

        // This node does not own the slot (nothing listens to it here), so a promoted envelope parked at the
        // companion queue is forwarded to the slot -- through this sender, whose second send fails.
        _slotSender = Substitute.For<ISendingAgent>();
        _slotSender.Destination.Returns(TheSlot);
        _slotSender.EnqueueOutgoingAsync(Arg.Any<Envelope>())
            .Returns(ValueTask.CompletedTask, ValueTask.FromException(new DivideByZeroException()));
        ((EndpointCollection)runtime.Endpoints).StoreSendingAgent(_slotSender);

        _store = Substitute.For<IMessageStore>();
        _store.Inbox.Returns(Substitute.For<IMessageInbox>());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Envelope promoted(string name)
    {
        return new Envelope(new PartitionedRetryMessage(name))
        {
            Destination = TheCompanionQueue,
            Store = _store
        };
    }

    [Fact]
    public async Task only_the_envelopes_not_yet_forwarded_are_released_and_at_their_parked_address()
    {
        var forwarded = promoted("first");
        var failed = promoted("second");
        var neverTried = promoted("third");

        await _host.GetRuntime().EnqueueDirectlyAsync([forwarded, failed, neverTried]);

        await _store.Received(1).ReassignIncomingAsync(TransportConstants.AnyNode,
            Arg.Is<IReadOnlyList<Envelope>>(released =>
                released.Select(x => x.Id).OrderBy(x => x)
                    .SequenceEqual(new[] { failed.Id, neverTried.Id }.OrderBy(x => x))
                && released.All(x => x.Destination == TheCompanionQueue)));
    }
}
