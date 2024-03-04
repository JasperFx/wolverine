using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Lamar;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using TestingSupport.Compliance;
using Weasel.Core;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Marten.Codegen;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace PersistenceTests.Marten;

public class transactional_frame_end_to_end : PostgresqlContext
{
    [Fact]
    public async Task the_transactional_middleware_works()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.Services.AddMarten(o =>
            {
                o.Connection(Servers.PostgresConnectionString);
                o.AutoCreateSchemaObjects = AutoCreate.All;
            }).IntegrateWithWolverine();
        });

        var command = new CreateDocCommand();
        await host.InvokeAsync(command);

        await using var query = host.Get<IQuerySession>();
        query.Load<FakeDoc>(command.Id)
            .ShouldNotBeNull();
    }

    public static async Task Using_CommandsAreTransactional()
    {
        #region sample_Using_CommandsAreTransactional

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // And actually use the policy
                opts.Policies.Add<CommandsAreTransactional>();
            }).StartAsync();

        #endregion
    }
}

public class CreateDocCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class CreateDocCommandHandler
{
    [Transactional]
    public void Handle(CreateDocCommand message, IDocumentSession session)
    {
        session.Store(new FakeDoc { Id = message.Id });
    }
}

public class UsingDocumentSessionHandler
{
    // Take in IDocumentStore as a constructor argument
    public UsingDocumentSessionHandler(IDocumentStore store)
    {
    }

    // Take in IDocumentSession as an argument
    public void Handle(Message1 message, IDocumentSession session)
    {
    }
}

#region sample_CommandsAreTransactional

public class CommandsAreTransactional : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IContainer container)
    {
        // Important! Create a brand new TransactionalFrame
        // for each chain
        chains
            .Where(chain => chain.MessageType.Name.EndsWith("Command"))
            .Each(chain => chain.Middleware.Add(new TransactionalFrame(chain)));
    }
}

#endregion