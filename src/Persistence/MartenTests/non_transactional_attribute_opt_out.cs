using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests;

public class non_transactional_attribute_opt_out : PostgresqlContext
{
    [Fact]
    public async Task handler_with_non_transactional_attribute_should_not_be_transactional()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var runtime = host.GetRuntime();

        // A handler that uses IDocumentSession but is decorated with [NonTransactional]
        // should NOT have transactional middleware applied
        runtime.Handlers.ChainFor<NonTransactionalCommand>()
            .IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public async Task handler_without_non_transactional_attribute_should_still_be_transactional()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var runtime = host.GetRuntime();

        // A normal handler that uses IDocumentSession should still get
        // transactional middleware from AutoApplyTransactions
        runtime.Handlers.ChainFor<TransactionalCommand>()
            .IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public async Task non_transactional_attribute_on_handler_class_should_opt_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var runtime = host.GetRuntime();

        // [NonTransactional] on the handler class should also opt out
        runtime.Handlers.ChainFor<NonTransactionalClassCommand>()
            .IsTransactional.ShouldBeFalse();
    }
}

public record NonTransactionalCommand(Guid Id);
public record TransactionalCommand(Guid Id);
public record NonTransactionalClassCommand(Guid Id);

public static class NonTransactionalCommandHandler
{
    [NonTransactional]
    public static void Handle(NonTransactionalCommand command, IDocumentSession session)
    {
        session.Store(new NonTransactionalDoc { Id = command.Id });
    }
}

public static class TransactionalCommandHandler
{
    public static void Handle(TransactionalCommand command, IDocumentSession session)
    {
        session.Store(new NonTransactionalDoc { Id = command.Id });
    }
}

[NonTransactional]
public static class NonTransactionalClassCommandHandler
{
    public static void Handle(NonTransactionalClassCommand command, IDocumentSession session)
    {
        session.Store(new NonTransactionalDoc { Id = command.Id });
    }
}

public class NonTransactionalDoc
{
    public Guid Id { get; set; }
}
