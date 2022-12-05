using System;
using System.Linq;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;
using Xunit;

namespace PersistenceTests.SqlServer.Persistence;

[MessageIdentity("Message1")]
public class Message1
{
    public Guid Id = Guid.NewGuid();
}

public class SqlServerBackedMessageStoreTests : SqlServerContext, IAsyncLifetime
{
    private Envelope persisted;
    private Envelope theEnvelope;

    private IHost theHost;

    public override Task DisposeAsync()
    {
        return theHost.StopAsync();
    }

    protected override async Task initialize()
    {
        theHost = WolverineHost.For(opts => { opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString); });

        await theHost.ResetResourceState();

        theEnvelope = ObjectMother.Envelope();
        theEnvelope.Message = new Message1();
        theEnvelope.ScheduledTime = DateTime.Today.ToUniversalTime().AddDays(1);
        theEnvelope.CorrelationId = Guid.NewGuid().ToString();
        theEnvelope.ConversationId = Guid.NewGuid();
        theEnvelope.ParentId = Guid.NewGuid().ToString();

        theHost.Get<IMessageStore>().ScheduleJobAsync(theEnvelope).Wait(3.Seconds());

        var persistor = theHost.Get<SqlServerMessageStore>();

        persisted = (await persistor.Admin.AllIncomingAsync())
            .FirstOrDefault(x => x.Id == theEnvelope.Id);
    }

    [Fact]
    public void should_be_in_scheduled_status()
    {
        persisted.Status.ShouldBe(EnvelopeStatus.Scheduled);
    }

    [Fact]
    public void should_bring_across_correlation_information()
    {
        persisted.CorrelationId.ShouldBe(theEnvelope.CorrelationId);
        persisted.ParentId.ShouldBe(theEnvelope.ParentId);
        persisted.ConversationId.ShouldBe(theEnvelope.ConversationId);
    }

    [Fact]
    public void should_be_owned_by_any_node()
    {
        persisted.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public void should_persist_the_scheduled_envelope()
    {
        persisted.ShouldNotBeNull();
    }
}