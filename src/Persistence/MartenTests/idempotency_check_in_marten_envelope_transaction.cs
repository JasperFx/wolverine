using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MartenTests;

public class idempotency_check_in_marten_envelope_transaction : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "idempotent";
                }).IntegrateWithWolverine();
            }).StartAsync();

        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task happy_path_eager_idempotency()
    {
        var runtime = _host.GetRuntime();
        var envelope = ObjectMother.Envelope();

        var context = new MessageContext(runtime);
        context.ReadEnvelope(envelope, Substitute.For<IChannelCallback>());

        using var session = _host.DocumentStore().LightweightSession();
        var transaction = new MartenEnvelopeTransaction(session, context);

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, new DurabilitySettings(), CancellationToken.None);
        ok.ShouldBeTrue();

        var persisted = (await runtime.Storage.Admin.AllIncomingAsync()).Single(x => x.Id == envelope.Id);
        persisted.Data.Length.ShouldBe(0);
        persisted.Destination.ShouldBe(envelope.Destination);
        persisted.MessageType.ShouldBe(envelope.MessageType);
        persisted.Status.ShouldBe(EnvelopeStatus.Handled);
        persisted.KeepUntil.HasValue.ShouldBeTrue();
        
    }
    
    [Fact]
    public async Task sad_path_eager_idempotency()
    {
        var runtime = _host.GetRuntime();
        var envelope = ObjectMother.Envelope();
        envelope.Id = Guid.NewGuid();

        var context = new MessageContext(runtime);
        context.ReadEnvelope(envelope, Substitute.For<IChannelCallback>());

        using var session = _host.DocumentStore().LightweightSession();
        var transaction = new MartenEnvelopeTransaction(session, context);

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, new DurabilitySettings(), CancellationToken.None);
        ok.ShouldBeTrue();

        // Kind of resetting it here
        envelope.WasPersistedInInbox = false;
        
        var secondTime = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, new DurabilitySettings(), CancellationToken.None);
        secondTime.ShouldBeFalse();
    }
}

public class idempotency_with_inline_or_buffered_endpoints_end_to_end
{
    [Theory]
    [InlineData(IdempotencyStyle.Optimistic)]
    [InlineData(IdempotencyStyle.Eager)]
    public async Task happy_and_sad_path(IdempotencyStyle idempotency)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // TODO -- make this the default
                opts.OnException<DuplicateIncomingEnvelopeException>().Discard();
                opts.Policies.AutoApplyTransactions(idempotency);

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "idempotent";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var messageId = Guid.NewGuid();
        var tracked1 = await host.SendMessageAndWaitAsync(new MaybeIdempotent(messageId));

        // First time through should be perfectly fine
        var sentMessage = tracked1.Executed.SingleEnvelope<MaybeIdempotent>();

        var runtime = host.GetRuntime();
        var circuit = runtime.Endpoints.FindListenerCircuit(sentMessage.Destination);

        var tracked2 = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
            {
                sentMessage.WasPersistedInInbox = false;
                sentMessage.Attempts = 0;
                return circuit.EnqueueDirectlyAsync([sentMessage]);
            });

        tracked2.Discarded.SingleEnvelope<MaybeIdempotent>().ShouldNotBeNull();
    }
    
    [Theory]
    [InlineData(IdempotencyStyle.Optimistic)]
    [InlineData(IdempotencyStyle.Eager)]
    public async Task happy_and_sad_path_with_message_and_destination_tracking(IdempotencyStyle idempotency)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // TODO -- make this the default
                opts.OnException<DuplicateIncomingEnvelopeException>().Discard();
                opts.Policies.AutoApplyTransactions(idempotency);

                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "idempotent";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var messageId = Guid.NewGuid();
        var tracked1 = await host.SendMessageAndWaitAsync(new MaybeIdempotent(messageId));

        // First time through should be perfectly fine
        var sentMessage = tracked1.Executed.SingleEnvelope<MaybeIdempotent>();

        var runtime = host.GetRuntime();
        var circuit = runtime.Endpoints.FindListenerCircuit(sentMessage.Destination);

        var tracked2 = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
            {
                sentMessage.WasPersistedInInbox = false;
                sentMessage.Attempts = 0;
                return circuit.EnqueueDirectlyAsync([sentMessage]);
            });

        tracked2.Discarded.SingleEnvelope<MaybeIdempotent>().ShouldNotBeNull();
    }
}

public record MaybeIdempotent(Guid Id);

public static class MaybeIdempotentHandler
{
    public static StoreDoc<MaybeIdempotent> Handle(MaybeIdempotent message)
    {
        return MartenOps.Store(message);
    }
}

public class MaybeIdempotentDoc
{
    public Guid Id { get; set; }
}