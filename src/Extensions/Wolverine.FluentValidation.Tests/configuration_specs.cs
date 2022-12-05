using FluentValidation;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.FluentValidation.Internals;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.FluentValidation.Tests;

public class configuration_specs
{
    [Fact]
    public async Task register_validators_in_application_assembly()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();

        var container = (IContainer)host.Services;

        // No args, this needs to be a singleton
        container.Model.For<IValidator<Command1>>().Default
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);

        container.Model.For<IValidator<Command2>>().Default
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task add_the_default_services()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();

        var container = (IContainer)host.Services;

        container.GetInstance<IFailureAction<Command1>>()
            .ShouldBeOfType<FailureAction<Command1>>();
    }

    [Fact]
    public async Task place_or_not_place_the_middleware_correctly()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();

        var handlers = host.Services.GetRequiredService<IWolverineRuntime>()
            .As<WolverineRuntime>().Options.Handlers.As<HandlerGraph>();

        handlers.ChainFor<Command1>().Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor) &&
                      x.Method.Name == nameof(FluentValidationExecutor.ExecuteMany))
            .ShouldBeTrue();

        handlers.ChainFor<Command2>().Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor) &&
                      x.Method.Name == nameof(FluentValidationExecutor.ExecuteOne))
            .ShouldBeTrue();

        // No validators here
        handlers.ChainFor<Command3>().Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor))
            .ShouldBeFalse();
    }
}

public class Command1
{
    public string Name { get; set; }
    public string Color { get; set; }

    public int Number { get; set; }
}

public class Command1Validator : AbstractValidator<Command1>
{
    public Command1Validator()
    {
        RuleFor(x => x.Name).NotNull().NotEmpty();
        RuleFor(x => x.Color).NotNull().NotEmpty();
    }
}

public class Command1Validator2 : AbstractValidator<Command1>
{
    public Command1Validator2()
    {
        RuleFor(x => x.Number).GreaterThan(3);
    }
}

public interface IDataService
{
}

public class DataService : IDataService
{
}

public class Command2
{
    public string Name { get; set; }
    public string Color { get; set; }
}

public class FancyCommand2Validator : AbstractValidator<Command2>
{
    private readonly IDataService _service;

    public FancyCommand2Validator(IDataService service)
    {
        _service = service;

        RuleFor(x => x.Name).NotNull();
    }
}

public record Command3; // Always valid

public class CommandHandler
{
    public void Handle(Command1 command)
    {
    }

    public void Handle(Command2 command)
    {
    }

    public void Handle(Command3 command)
    {
    }
}