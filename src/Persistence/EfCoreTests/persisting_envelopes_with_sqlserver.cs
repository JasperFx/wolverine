using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

[Collection("sqlserver")]
public class persisting_envelopes_with_sqlserver : IAsyncLifetime
{
    private IHost _host;
    private Envelope theIncomingEnvelope;
    private Envelope theOutgoingEnvelope;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContext<SampleMappedDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddDbContext<SampleDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync();

        theIncomingEnvelope = new Envelope
        {
            Id = Guid.NewGuid(),
            Status = EnvelopeStatus.Handled,
            OwnerId = 5,
            ScheduledTime = new DateTimeOffset(DateTime.Today.AddHours(5)),
            Attempts = 2,
            Data = [1, 2, 3, 4],
            ConversationId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString(),
            ParentId = Guid.NewGuid().ToString(),
            SagaId = Guid.NewGuid().ToString(),
            MessageType = "foo",
            ContentType = "application/json",
            ReplyRequested = "bar",
            AckRequested = true,
            ReplyUri = new Uri("rabbitmq://queue/incoming"),
            Destination = new Uri("rabbitmq://queue/arrived"),
            DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(3)),
            SentAt = new DateTimeOffset(DateTime.Today.AddHours(2))
        };

        theOutgoingEnvelope = new Envelope
        {
            Id = Guid.NewGuid(),
            Status = EnvelopeStatus.Handled,
            OwnerId = 5,
            ScheduledTime = new DateTimeOffset(DateTime.Today.AddHours(5)),
            Attempts = 2,
            Data = [1, 2, 3, 4],
            ConversationId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString(),
            ParentId = Guid.NewGuid().ToString(),
            SagaId = Guid.NewGuid().ToString(),
            MessageType = "foo",
            ContentType = "application/json",
            ReplyRequested = "bar",
            AckRequested = true,
            ReplyUri = new Uri("rabbitmq://queue/incoming"),
            Destination = new Uri("rabbitmq://queue/arrived"),
            DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(3)),
            SentAt = new DateTimeOffset(DateTime.Today.AddHours(2))
        };

        using var nested = _host.Services.CreateScope();
        var context = nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>();

        context.Add(new IncomingMessage(theIncomingEnvelope));
        context.Add(new OutgoingMessage(theOutgoingEnvelope));

        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void is_wolverine_enabled()
    {
        using var nested = _host.Services.CreateScope();
        nested.ServiceProvider.GetRequiredService<SampleDbContext>().IsWolverineEnabled().ShouldBeFalse();
        nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>().IsWolverineEnabled().ShouldBeTrue();
    }

    [Fact]
    public void selectively_building_envelope_transaction()
    {
        using var nested = _host.Services.CreateScope();
        var runtime = _host.GetRuntime();
        var context = new MessageContext(runtime);

        nested.ServiceProvider.GetRequiredService<SampleDbContext>().BuildTransaction(context)
            .ShouldBeOfType<RawDatabaseEnvelopeTransaction>();
        nested.ServiceProvider.GetRequiredService<SampleMappedDbContext>().BuildTransaction(context)
            .ShouldBeOfType<MappedEnvelopeTransaction>();
    }

    [Fact]
    public async Task mapping_to_incoming_envelopes()
    {
        var storage = _host.Services.GetRequiredService<IMessageStore>();
        var envelopes = await storage.Admin.AllIncomingAsync();

        var envelope = envelopes.FirstOrDefault(x => x.Id == theIncomingEnvelope.Id);
        envelope.ShouldNotBeNull();

        envelope.Status.ShouldBe(theIncomingEnvelope.Status);
        envelope.OwnerId.ShouldBe(theIncomingEnvelope.OwnerId);
        envelope.ScheduledTime.ShouldBe(theIncomingEnvelope.ScheduledTime);
        envelope.Attempts.ShouldBe(theIncomingEnvelope.Attempts);
        envelope.Data.ShouldBe(theIncomingEnvelope.Data);
        envelope.ConversationId.ShouldBe(theIncomingEnvelope.ConversationId);
        envelope.CorrelationId.ShouldBe(theIncomingEnvelope.CorrelationId);
        envelope.ParentId.ShouldBe(theIncomingEnvelope.ParentId);
        envelope.SagaId.ShouldBe(theIncomingEnvelope.SagaId);
        envelope.MessageType.ShouldBe(theIncomingEnvelope.MessageType);
        envelope.ContentType.ShouldBe(theIncomingEnvelope.ContentType);
        envelope.ReplyRequested.ShouldBe(theIncomingEnvelope.ReplyRequested);
        envelope.AckRequested.ShouldBe(theIncomingEnvelope.AckRequested);
        envelope.ReplyUri.ShouldBe(theIncomingEnvelope.ReplyUri);
        envelope.Destination.ShouldBe(theIncomingEnvelope.Destination);
        envelope.SentAt.ShouldBe(theIncomingEnvelope.SentAt);
    }

    [Fact]
    public async Task mapping_to_outgoing_envelopes()
    {
        var storage = _host.Services.GetRequiredService<IMessageStore>();
        var envelopes = await storage.Admin.AllOutgoingAsync();

        var envelope = envelopes.FirstOrDefault(x => x.Id == theOutgoingEnvelope.Id);
        envelope.ShouldNotBeNull();

        envelope.OwnerId.ShouldBe(theOutgoingEnvelope.OwnerId);
        envelope.Attempts.ShouldBe(theOutgoingEnvelope.Attempts);
        envelope.Data.ShouldBe(theOutgoingEnvelope.Data);
        envelope.ConversationId.ShouldBe(theOutgoingEnvelope.ConversationId);
        envelope.CorrelationId.ShouldBe(theOutgoingEnvelope.CorrelationId);
        envelope.ParentId.ShouldBe(theOutgoingEnvelope.ParentId);
        envelope.SagaId.ShouldBe(theOutgoingEnvelope.SagaId);
        envelope.MessageType.ShouldBe(theOutgoingEnvelope.MessageType);
        envelope.ContentType.ShouldBe(theOutgoingEnvelope.ContentType);
        envelope.ReplyRequested.ShouldBe(theOutgoingEnvelope.ReplyRequested);
        envelope.AckRequested.ShouldBe(theOutgoingEnvelope.AckRequested);
        envelope.ReplyUri.ShouldBe(theOutgoingEnvelope.ReplyUri);
        envelope.Destination.ShouldBe(theOutgoingEnvelope.Destination);
        envelope.DeliverBy.ShouldBe(theOutgoingEnvelope.DeliverBy);
        envelope.SentAt.ShouldBe(theOutgoingEnvelope.SentAt);
    }
}