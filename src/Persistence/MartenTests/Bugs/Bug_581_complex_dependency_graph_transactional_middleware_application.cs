using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.Bugs;

public class Bug_581_complex_dependency_graph_transactional_middleware_application : PostgresqlContext
{
    private readonly ITestOutputHelper _output;

    public Bug_581_complex_dependency_graph_transactional_middleware_application(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task apply_transactional_middleware_when_session_is_used_internally_in_dependency_tree()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "users";
                }).IntegrateWithWolverine();

                opts.Services.AddScoped<IUserService, UserService>();
                opts.Services.AddScoped<IUserRepository, UserRepository>();

                opts.Policies.AutoApplyTransactions();

                opts.Policies.ForMessagesOfType<CreateUser2>()
                    .AddMiddleware<DoSomethingWithMartenMiddleware>();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var handlers = runtime.Handlers;

        var martenPersistenceFrameProvider = new MartenPersistenceFrameProvider();
        martenPersistenceFrameProvider
            .CanApply(handlers.HandlerFor<CreateUser>().As<MessageHandler>().Chain, host.Services.GetRequiredService<IServiceContainer>()).ShouldBeTrue();

        // For middleware too
        martenPersistenceFrameProvider
            .CanApply(handlers.HandlerFor<CreateUser2>().As<MessageHandler>().Chain, host.Services.GetRequiredService<IServiceContainer>()).ShouldBeTrue();

    }
}

public class DoSomethingWithMartenMiddleware
{
    private readonly IUserService _service;

    public DoSomethingWithMartenMiddleware(IUserService service)
    {
        _service = service;
    }

    public Task Before(CreateUser2 command)
    {
        return _service.CreateUser(command.Name);
    }
}

public class CreateUserHandler
{
    private readonly IUserService _service;

    public CreateUserHandler(IUserService service)
    {
        _service = service;
    }

    public async Task Handle(CreateUser command)
    {
        await _service.CreateUser(command.Name);
    }
}

public static class CreateUser2Handler
{
    public static void Handle(CreateUser2 user2)
    {
        Debug.WriteLine("Got user 2");
    }
}

public record CreateUser(string Name);
public record CreateUser2(string Name);

public interface IUserService
{
    Task CreateUser(string name);
}

public class UserService : IUserService
{
    private readonly IUserRepository _repository;

    public UserService(IUserRepository repository)
    {
        _repository = repository;
    }

    public Task CreateUser(string name)
    {
        var user = new User { Name = name };
        return _repository.Store(user);
    }
}

public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public interface IUserRepository
{
    Task Store(User user);
}

public class UserRepository : IUserRepository
{
    private readonly IDocumentSession _session;

    public UserRepository(IDocumentSession session)
    {
        _session = session;
    }

    public Task Store(User user)
    {
        _session.Store(user);
        return Task.CompletedTask;
    }
}