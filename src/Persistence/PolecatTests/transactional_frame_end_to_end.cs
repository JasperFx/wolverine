using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;

namespace PolecatTests;

public class transactional_frame_end_to_end
{
    [Fact]
    public async Task the_transactional_middleware_works()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "transactional";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        var command = new PcCreateDocCommand();
        await host.InvokeAsync(command);

        await using var query = store.QuerySession();
        (await query.LoadAsync<PcFakeDoc>(command.Id))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task the_transactional_middleware_works_with_document_operations()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "transactional";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        var command = new PcCreateDocCommand2();
        await host.InvokeAsync(command);

        await using var query = store.QuerySession();
        (await query.LoadAsync<PcFakeDoc>(command.Id))
            .ShouldNotBeNull();
    }
}

public class PcCreateDocCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class PcCreateDocCommand2
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class PcCreateDocCommand2Handler
{
    [Transactional]
    public void Handle(PcCreateDocCommand2 message, IDocumentSession session)
    {
        session.Store(new PcFakeDoc { Id = message.Id });
    }
}

public class PcCreateDocCommandHandler
{
    [Transactional]
    public void Handle(PcCreateDocCommand message, IDocumentSession session)
    {
        session.Store(new PcFakeDoc { Id = message.Id });
    }
}

public class PcFakeDoc
{
    public Guid Id { get; set; }
}
