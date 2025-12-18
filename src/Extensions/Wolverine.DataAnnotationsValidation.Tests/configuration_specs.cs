using System.ComponentModel.DataAnnotations;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.DataAnnotationsValidation.Internals;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.DataAnnotationsValidation.Tests;

public class configuration_specs
{
    [Fact]
    public async Task add_the_default_services()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseDataAnnotationsValidation();
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
                opts.UseDataAnnotationsValidation();
            }).StartAsync();

        var wolverineOptions = host.Services.GetRequiredService<IWolverineRuntime>()
            .As<WolverineRuntime>().Options;

        // Not proud of this code
        var handlers = (HandlerGraph)typeof(WolverineOptions)
            .GetProperty(nameof(HandlerGraph), BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(wolverineOptions);

        handlers.ChainFor<Command1>().Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(DataAnnotationsValidationExecutor) &&
                      x.Method.Name == nameof(DataAnnotationsValidationExecutor.Validate))
            .ShouldBeTrue();
        
        // No validators here, but the middleware will always be applied
        handlers.ChainFor<Command3>().Middleware.OfType<MethodCall>()
            .Any(x => x.HandlerType == typeof(DataAnnotationsValidationExecutor))
            .ShouldBeTrue();
    }
}

public class Command1
{
    [Required]
    public string Name { get; set; }
    [Required]
    public string Color { get; set; }
    [Range(3, int.MaxValue)]
    public int Number { get; set; }
}

public class Command2 : IValidatableObject
{
    public string Name { get; set; }
    public string Color { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name is null) yield return new("Cannot be null", [nameof(Name)]);
    }
}

public record Command3; // Always valid

public class Command4
{
    [IsUniqueEmail]
    public required string Email { get; init; }
}

public class IsUniqueEmailAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        return value is not "existing@email.me";
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
}