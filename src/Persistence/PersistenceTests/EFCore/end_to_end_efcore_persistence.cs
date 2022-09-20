using System;
using System.Linq;
using System.Threading.Tasks;
using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Wolverine.Persistence.Durability;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Persistence.Testing.EFCore;

public class EFCorePersistenceContext : BaseContext
{
    public EFCorePersistenceContext() : base(false)
    {
        builder.ConfigureServices((c, services) =>
            {
                services.AddDbContext<SampleDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
            })
            .UseWolverine(options =>
            {
                options.Services.AddSingleton<PassRecorder>();
                options.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                options.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            });
    }
}

[Collection("sqlserver")]
public class end_to_end_efcore_persistence : IClassFixture<EFCorePersistenceContext>
{
    public end_to_end_efcore_persistence(EFCorePersistenceContext context)
    {
        Host = context.theHost;
    }

    public IHost Host { get; }

    [Fact]
    public async Task persist_an_outgoing_envelope()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = new byte[] { 1, 2, 3, 4 },
            OwnerId = 5,
            Destination = TransportConstants.RetryUri,
            DeliverBy = new DateTimeOffset(DateTime.Today),
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        var context = Host.Services.GetRequiredService<SampleDbContext>();
        var messaging = Host.Services.GetRequiredService<IMessageContext>();

        var transaction = new EfCoreEnvelopeOutbox(context, messaging);

        await transaction.PersistAsync(envelope);
        await context.SaveChangesAndFlushMessagesAsync(messaging);

        var persisted = await Host.Services.GetRequiredService<IEnvelopePersistence>()
            .Admin.AllOutgoingAsync();

        var loadedEnvelope = persisted.Single();

        loadedEnvelope.Id.ShouldBe(envelope.Id);

        loadedEnvelope.Destination.ShouldBe(envelope.Destination);
        loadedEnvelope.DeliverBy.ShouldBe(envelope.DeliverBy);
        loadedEnvelope.Data.ShouldBe(envelope.Data);


        loadedEnvelope.OwnerId.ShouldBe(envelope.OwnerId);
    }

    [Fact]
    public async Task persist_an_incoming_envelope()
    {
        await Host.ResetResourceState();

        var envelope = new Envelope
        {
            Data = new byte[] { 1, 2, 3, 4 },
            OwnerId = 5,
            ScheduledTime = DateTime.Today.AddDays(1),
            DeliverBy = new DateTimeOffset(DateTime.Today),
            Status = EnvelopeStatus.Scheduled,
            Attempts = 2,
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        var context = Host.Services.GetRequiredService<SampleDbContext>();
        var messaging = Host.Services.GetRequiredService<IMessageContext>();

        var transaction = new EfCoreEnvelopeOutbox(context, messaging);

        await transaction.ScheduleJobAsync(envelope);
        await context.SaveChangesAndFlushMessagesAsync(messaging);

        var persisted = await Host.Services.GetRequiredService<IEnvelopePersistence>()
            .Admin.AllIncomingAsync();

        var loadedEnvelope = persisted.Single();

        loadedEnvelope.Id.ShouldBe(envelope.Id);

        loadedEnvelope.Destination.ShouldBe(envelope.Destination);
        loadedEnvelope.ScheduledTime.ShouldBe(envelope.ScheduledTime);
        loadedEnvelope.Data.ShouldBe(envelope.Data);
        loadedEnvelope.OwnerId.ShouldBe(envelope.OwnerId);
        loadedEnvelope.Attempts.ShouldBe(envelope.Attempts);
    }
}

public class PassRecorder
{
    private readonly TaskCompletionSource<Pass> _completion = new();

    public Task<Pass> Actual => _completion.Task;

    public void Record(Pass pass)
    {
        _completion.SetResult(pass);
    }
}

public class PassHandler
{
    private readonly PassRecorder _recorder;

    public PassHandler(PassRecorder recorder)
    {
        _recorder = recorder;
    }

    public void Handle(Pass pass)
    {
        _recorder.Record(pass);
    }
}

public class Pass
{
    public string From { get; set; }
    public string To { get; set; }
}
