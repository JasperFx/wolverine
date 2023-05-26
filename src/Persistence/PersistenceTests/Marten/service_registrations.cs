using IntegrationTests;
using Lamar;
using Marten;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Weasel.Core;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;

namespace PersistenceTests.Marten;

public class service_registrations : PostgresqlContext
{
    [Fact]
    public void basic_registrations()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(o =>
                {
                    o.Connection(Servers.PostgresConnectionString);
                    o.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine();
            }).Start();

        var container = (IContainer)host.Services;

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
        using var runtime = WolverineHost.For(opts =>
        {
            opts.Services.AddMarten(o =>
            {
                o.Connection(Servers.PostgresConnectionString);
                o.AutoCreateSchemaObjects = AutoCreate.All;
            }).IntegrateWithWolverine();
        });

        var doc = new FakeDoc { Id = Guid.NewGuid() };


        using (var session = runtime.Get<IDocumentSession>())
        {
            session.Store(doc);
            session.SaveChanges();
        }

        using (var query = runtime.Get<IQuerySession>())
        {
            query.Load<FakeDoc>(doc.Id).ShouldNotBeNull();
        }
    }
}

public class FakeDoc
{
    public Guid Id { get; set; }
}