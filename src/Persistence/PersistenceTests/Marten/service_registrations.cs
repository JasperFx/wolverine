using System;
using System.Linq;
using IntegrationTests;
using Lamar;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Persistence.Testing.Marten;

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

        container.Model.For<IEnvelopePersistence>()
            .Default.ImplementationType.ShouldBe(typeof(PostgresqlEnvelopePersistence));

        container.Model.For<IWolverineExtension>().Instances
            .Any(x => x.ImplementationType == typeof(MartenIntegration))
            .ShouldBeTrue();

        container.GetInstance<PostgresqlSettings>()
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
            }).Start();

        var container = (IContainer)host.Services;

        container.Model.For<IEnvelopePersistence>()
            .Default.ImplementationType.ShouldBe(typeof(PostgresqlEnvelopePersistence));

        container.Model.For<IWolverineExtension>().Instances
            .Any(x => x.ImplementationType == typeof(MartenIntegration))
            .ShouldBeTrue();

        container.GetInstance<PostgresqlSettings>()
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
