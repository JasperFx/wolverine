using System.Runtime.CompilerServices;
using FluentValidation;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.FluentValidation.Internals;

public static class FluentValidationHttpExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<IResult> ExecuteOne<T>(IValidator<T> validator, IProblemDetailSource<T> source, T message)
    {
        var result = await validator.ValidateAsync(message);
        if (result.Errors.Any())
        {
            var details = source.Create(message, result.Errors);
            return Results.Problem(details);
        }

        return WolverineContinue.Result();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<IResult> ExecuteMany<T>(
        IReadOnlyList<IValidator<T>> validators,
        IProblemDetailSource<T> source, 
        T message)
    {
        var validationFailureTasks = validators
            .Select(validator => validator.ValidateAsync(message));
        var validationFailures = await Task.WhenAll(validationFailureTasks);
        var failures = validationFailures.SelectMany(validationResult => validationResult.Errors)
            .Where(validationFailure => validationFailure != null)
            .ToList();

        if (failures.Any())
        {
            var problems = source.Create(message, failures);
            return Results.Problem(problems);
        }
        
        return WolverineContinue.Result();
    }
}