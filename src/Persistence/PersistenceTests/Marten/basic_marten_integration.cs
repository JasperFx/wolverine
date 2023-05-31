using IntegrationTests;
using JasperFx.Core.Reflection;
using Lamar;
using Marten;
using Marten.Internal.Sessions;
using Marten.Storage;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Xunit;

namespace PersistenceTests.Marten;

public class basic_marten_integration : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(o =>
                {
                    o.Connection(Servers.PostgresConnectionString);
                    o.AutoCreateSchemaObjects = AutoCreate.All;
                }).UseLightweightSessions().IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
    }


    [Fact]
    public void basic_registrations()
    {
        var container = (IContainer)theHost.Services;

        container.Model.For<IMessageStore>()
            .Default.ImplementationType.ShouldBe(typeof(PostgresqlMessageStore));

        container.Model.For<IWolverineExtension>().Instances
            .Any(x => x.ImplementationType == typeof(MartenIntegration))
            .ShouldBeTrue();

        container.GetInstance<IMessageStore>().ShouldBeOfType<PostgresqlMessageStore>()
            .SchemaName.ShouldBe("public");
    }

    [Fact]
    public void override_schema_name()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(o =>
                {
                    o.Connection(Servers.PostgresConnectionString);
                    o.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine("wolverine");

                opts.Services.AddResourceSetupOnStartup();
            }).Start();

        var container = (IContainer)host.Services;

        container.Model.For<IMessageStore>()
            .Default.ImplementationType.ShouldBe(typeof(PostgresqlMessageStore));

        container.Model.For<IWolverineExtension>().Instances
            .Any(x => x.ImplementationType == typeof(MartenIntegration))
            .ShouldBeTrue();

        container.GetInstance<IMessageStore>().ShouldBeOfType<PostgresqlMessageStore>()
            .SchemaName.ShouldBe("wolverine");
    }

    [Fact]
    public void registers_document_store_in_a_usable_way()
    {

        var doc = new FakeDoc { Id = Guid.NewGuid() };


        using (var session = theHost.Get<IDocumentSession>())
        {
            session.Store(doc);
            session.SaveChanges();
        }

        using (var query = theHost.Get<IQuerySession>())
        {
            query.Load<FakeDoc>(doc.Id).ShouldNotBeNull();
        }
    }

    [Fact]
    public void build_query_session_for_blank_tenant()
    {
        var runtime = theHost.Get<IWolverineRuntime>();
        var factory = theHost.Get<OutboxedSessionFactory>();
        var messageContext = new MessageContext(runtime);    
        using var session = factory.QuerySession(messageContext);
        
        session.As<QuerySession>().TenantId.ShouldBe(Tenancy.DefaultTenantId);
    }

    [Fact]
    public void build_query_session_for_non_default_tenant()
    {
        var runtime = theHost.Get<IWolverineRuntime>();
        var factory = theHost.Get<OutboxedSessionFactory>();
        var messageContext = new MessageContext(runtime);
        messageContext.TenantId = "tenant1";
        
        using var session = factory.QuerySession(messageContext);
        
        session.As<QuerySession>().TenantId.ShouldBe("tenant1");
    }

    [Fact]
    public void build_session_for_blank_tenant()
    {
        var runtime = theHost.Get<IWolverineRuntime>();
        var factory = theHost.Get<OutboxedSessionFactory>();
        var messageContext = new MessageContext(runtime);    
        using var session = factory.OpenSession(messageContext);
        
        session.As<QuerySession>().TenantId.ShouldBe(Tenancy.DefaultTenantId);
    }
    
    [Fact]
    public void build_document_session_for_non_default_tenant()
    {
        var runtime = theHost.Get<IWolverineRuntime>();
        var factory = theHost.Get<OutboxedSessionFactory>();
        var messageContext = new MessageContext(runtime);
        messageContext.TenantId = "tenant1";
        
        using var session = factory.OpenSession(messageContext);
        
        session.As<QuerySession>().TenantId.ShouldBe("tenant1");
    }
}

public class FakeDoc
{
    public Guid Id { get; set; }
}