using System.Reflection;
using FluentValidation;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.FluentValidation.Internals;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.FluentValidation.Tests;

public class configuration_specs : IDisposable
{
    // Store original values to restore after tests that modify global state
    private readonly CascadeMode _originalClassCascadeMode = ValidatorOptions.Global.DefaultClassLevelCascadeMode;
    private readonly CascadeMode _originalRuleCascadeMode = ValidatorOptions.Global.DefaultRuleLevelCascadeMode;
    private readonly Severity _originalSeverity = ValidatorOptions.Global.Severity;

    public void Dispose()
    {
        ValidatorOptions.Global.DefaultClassLevelCascadeMode = _originalClassCascadeMode;
        ValidatorOptions.Global.DefaultRuleLevelCascadeMode = _originalRuleCascadeMode;
        ValidatorOptions.Global.Severity = _originalSeverity;
    }

    [Fact]
    public async Task register_validators_in_application_assembly()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();

        var container = host.Services.GetRequiredService<IServiceContainer>();

        // No args, this needs to be a singleton
        container.DefaultFor<IValidator<Command1>>()!
            .Lifetime.ShouldBe(ServiceLifetime.Singleton);

        container.DefaultFor<IValidator<Command2>>()!
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
        
        host.Services.GetRequiredService<IFailureAction<Command1>>()
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

        var wolverineOptions = host.Services.GetRequiredService<IWolverineRuntime>()
            .As<WolverineRuntime>().Options;

        // Not proud of this code
        var handlers = (HandlerGraph)typeof(WolverineOptions)
            .GetProperty(nameof(HandlerGraph), BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(wolverineOptions)!;

        handlers.ChainFor<Command1>()!.Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor) &&
                      x.Method.Name == nameof(FluentValidationExecutor.ExecuteMany))
            .ShouldBeTrue();

        handlers.ChainFor<Command2>()!.Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor) &&
                      x.Method.Name == nameof(FluentValidationExecutor.ExecuteOne))
            .ShouldBeTrue();

        // No validators here
        handlers.ChainFor<Command3>()!.Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task configure_validator_options_via_action_overload()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation(fv =>
                {
                    fv.ValidatorOptions.DefaultRuleLevelCascadeMode = CascadeMode.Stop;
                    fv.ValidatorOptions.DefaultClassLevelCascadeMode = CascadeMode.Stop;
                    fv.ValidatorOptions.Severity = Severity.Warning;
                });

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();

        ValidatorOptions.Global.DefaultRuleLevelCascadeMode.ShouldBe(CascadeMode.Stop);
        ValidatorOptions.Global.DefaultClassLevelCascadeMode.ShouldBe(CascadeMode.Stop);
        ValidatorOptions.Global.Severity.ShouldBe(Severity.Warning);
    }

    [Fact]
    public async Task configure_registration_behavior_via_action_overload()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IValidator<Command1>, Command1Validator>();

                opts.UseFluentValidation(fv =>
                {
                    fv.RegistrationBehavior = RegistrationBehavior.ExplicitRegistration;
                });
            }).StartAsync();

        var container = host.Services.GetRequiredService<IServiceContainer>();

        // Only the explicitly registered validator should be present
        container.DefaultFor<IValidator<Command1>>()!
            .ImplementationType.ShouldBe(typeof(Command1Validator));
    }

    [Fact]
    public async Task action_overload_still_applies_middleware()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation(fv =>
                {
                    fv.ValidatorOptions.DefaultRuleLevelCascadeMode = CascadeMode.Stop;
                });

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();

        var wolverineOptions = host.Services.GetRequiredService<IWolverineRuntime>()
            .As<WolverineRuntime>().Options;

        var handlers = (HandlerGraph)typeof(WolverineOptions)
            .GetProperty(nameof(HandlerGraph), BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(wolverineOptions)!;

        // Middleware should still be wired up
        handlers.ChainFor<Command1>()!.Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(FluentValidationExecutor))
            .ShouldBeTrue();
    }

    [Fact]
    public void fluent_validation_configuration_defaults()
    {
        var config = new FluentValidationConfiguration();

        config.RegistrationBehavior.ShouldBe(RegistrationBehavior.DiscoverAndRegisterValidators);
        config.ValidatorOptions.ShouldBe(ValidatorOptions.Global);
    }
}

public class Command1
{
    public string Name { get; set; } = null!;
    public string Color { get; set; } = null!;

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
    Task<bool> IsUniqueEmail(string email);
    Task<bool> IsUniqueUsername(string userName);
}

public class DataService : IDataService
{
    public async Task<bool> IsUniqueEmail(string email)
    {
        var isUnique = email.Equals("new@email.me");
        return await Task.FromResult(isUnique);
    }

    public async Task<bool> IsUniqueUsername(string userName)
    {
        var isUnique = userName.Equals("UniqueUsername");
        return await Task.FromResult(isUnique);
    }
}

public class Command2
{
    public string Name { get; set; } = null!;
    public string Color { get; set; } = null!;
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

public class Command4
{
    public required string Email { get; init; }
}

public class Command4Validator : AbstractValidator<Command4>
{
    public Command4Validator(IDataService dataService)
    {
        RuleFor(x => x.Email).MustAsync(async (email, cancelation)
            => await dataService.IsUniqueEmail(email));
    }
}

public class Command5
{
    public required string Username { get; init; }
    public required string Email { get; init; }
}

public class Command5ValidatorAsync : AbstractValidator<Command5>
{
    public Command5ValidatorAsync(IDataService dataService)
    {
        RuleFor(x => x.Email).MustAsync(async (email, cancelation)
            => await dataService.IsUniqueEmail(email));
        RuleFor(x => x.Username).MustAsync(async (userName, cancelation)
            => await dataService.IsUniqueUsername(userName));
    }
}

public class Command5Validator : AbstractValidator<Command5>
{
    public Command5Validator(IDataService dataService)
    {
        RuleFor(x => x.Email).NotNull().NotEmpty();
        RuleFor(x => x.Username).NotNull().NotEmpty();
        ;
    }
}

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

    public void Handle(Command4 command)
    {
    }

    public void Handle(Command5 command)
    {
    }
}