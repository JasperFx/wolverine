using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MartenTests;

public class idempotency_check_on_inline_endpoints : IAsyncLifetime
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

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, CancellationToken.None);
        ok.ShouldBeTrue();

        var persisted = (await runtime.Storage.Admin.AllIncomingAsync()).Single(x => x.Id == envelope.Id);
        persisted.Data.Length.ShouldBe(0);
        persisted.Destination.ShouldBe(envelope.Destination);
        persisted.MessageType.ShouldBe(envelope.MessageType);
        persisted.Status.ShouldBe(EnvelopeStatus.Handled);
        
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

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, CancellationToken.None);
        ok.ShouldBeTrue();

        // Kind of resetting it here
        envelope.IsPersisted = false;
        
        var secondTime = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, CancellationToken.None);
        secondTime.ShouldBeFalse();

        
    }
}