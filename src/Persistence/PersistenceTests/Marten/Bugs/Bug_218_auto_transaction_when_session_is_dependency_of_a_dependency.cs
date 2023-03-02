using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten.Bugs;

public class Bug_218_auto_transaction_when_session_is_dependency_of_a_dependency : PostgresqlContext
{
    [Fact]
    public async Task should_apply_transaction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Services.AddScoped<IBug218Repository, Bug218Repository>();
            }).StartAsync();

        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new CreateBug218(id));

        using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var doc = await session.LoadAsync<Bug218>(id);

        doc.ShouldNotBeNull();
    }
}

public interface IBug218Repository
{
    void Store(Bug218 doc);
}

public class Bug218Repository : IBug218Repository
{
    private readonly IDocumentSession _session;

    public Bug218Repository(IDocumentSession session)
    {
        _session = session;
    }

    public void Store(Bug218 doc)
    {
        _session.Store(doc);
    }
}

public record CreateBug218(Guid Id);

public class CreateBug218Handler
{
    private readonly IBug218Repository _repository;

    public CreateBug218Handler(IBug218Repository repository)
    {
        _repository = repository;
    }

    public void Handle(CreateBug218 command)
    {
        var doc = new Bug218 { Id = command.Id };
        _repository.Store(doc);
    }
}

public class Bug218
{
    public Guid Id { get; set; }
}