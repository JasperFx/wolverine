using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace PolecatTests;

public class non_transactional_attribute_opt_out
{
    [Fact]
    public async Task handler_with_non_transactional_attribute_should_not_be_transactional()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "non_transactional";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var runtime = host.GetRuntime();

        runtime.Handlers.ChainFor<PcNonTransactionalCommand>()
            .IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public async Task handler_without_non_transactional_attribute_should_still_be_transactional()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "non_transactional";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var runtime = host.GetRuntime();

        runtime.Handlers.ChainFor<PcTransactionalCommand>()
            .IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public async Task non_transactional_attribute_on_handler_class_should_opt_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "non_transactional";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var runtime = host.GetRuntime();

        runtime.Handlers.ChainFor<PcNonTransactionalClassCommand>()
            .IsTransactional.ShouldBeFalse();
    }
}

public record PcNonTransactionalCommand(Guid Id);
public record PcTransactionalCommand(Guid Id);
public record PcNonTransactionalClassCommand(Guid Id);

public static class PcNonTransactionalCommandHandler
{
    [NonTransactional]
    public static void Handle(PcNonTransactionalCommand command, IDocumentSession session)
    {
        session.Store(new PcNonTransactionalDoc { Id = command.Id });
    }
}

public static class PcTransactionalCommandHandler
{
    public static void Handle(PcTransactionalCommand command, IDocumentSession session)
    {
        session.Store(new PcNonTransactionalDoc { Id = command.Id });
    }
}

[NonTransactional]
public static class PcNonTransactionalClassCommandHandler
{
    public static void Handle(PcNonTransactionalClassCommand command, IDocumentSession session)
    {
        session.Store(new PcNonTransactionalDoc { Id = command.Id });
    }
}

public class PcNonTransactionalDoc
{
    public Guid Id { get; set; }
}
