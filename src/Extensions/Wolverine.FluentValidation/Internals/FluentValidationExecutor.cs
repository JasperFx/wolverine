using System.Runtime.CompilerServices;
using FluentValidation;

namespace Wolverine.FluentValidation.Internals;

public static class FluentValidationExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task ExecuteOne<T>(IValidator<T> validator, IFailureAction<T> failureAction, T message)
    {
        var result = await validator.ValidateAsync(message);
        if (result.Errors.Any())
        {
            failureAction.Throw(message, result.Errors);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task ExecuteMany<T>(IReadOnlyList<IValidator<T>> validators, IFailureAction<T> failureAction,
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
            failureAction.Throw(message, failures);
        }
    }
}