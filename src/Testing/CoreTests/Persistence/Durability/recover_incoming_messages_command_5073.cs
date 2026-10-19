using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Persistence.Durability;

public class RecoverIncomingMessagesCommand5073Tests
{
    [Fact]
    public async Task OnlyEnqueuesEnvelopesReturnedByTheClaim()
    {
        var destination = new Uri("stub://recovery");
        var candidates = new[]
        {
            new Envelope { Id = Guid.NewGuid(), Destination = destination, Status = EnvelopeStatus.Incoming },
            new Envelope { Id = Guid.NewGuid(), Destination = destination, Status = EnvelopeStatus.Incoming }
        };
        var claimed = new[] { candidates[0] };

        var store = Substitute.For<IMessageStore>();
        store.LoadPageOfGloballyOwnedIncomingAsync(destination, Arg.Any<int>()).Returns(candidates);
        store.TryClaimIncomingAsync(Arg.Any<int>(), candidates)
            .Returns(Task.FromResult<IReadOnlyList<Envelope>>(claimed));

        var circuit = Substitute.For<IListenerCircuit>();
        circuit.Status.Returns(ListeningStatus.Accepting);
        circuit.QueueCount.Returns(0);
        circuit.Endpoint.Returns(new StubEndpoint(destination));
        circuit.EnqueueDirectlyAsync(Arg.Any<IEnumerable<Envelope>>()).Returns(Task.CompletedTask);

        var command = new RecoverIncomingMessagesCommand(store, new IncomingCount(destination, candidates.Length),
            circuit, new DurabilitySettings { AssignedNodeNumber = 45_001 }, NullLogger.Instance);

        await command.ExecuteAsync(Substitute.For<IWolverineRuntime>(), CancellationToken.None);

        await circuit.Received(1).EnqueueDirectlyAsync(
            Arg.Is<IEnumerable<Envelope>>(envelopes => envelopes.SequenceEqual(claimed)));
    }

    private sealed class StubEndpoint(Uri uri) : Endpoint(uri, EndpointRole.Application)
    {
        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            throw new NotSupportedException();
        }

        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            throw new NotSupportedException();
        }

        protected override bool supportsMode(EndpointMode mode)
        {
            return true;
        }
    }
}
