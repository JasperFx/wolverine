using System.Runtime.CompilerServices;
using FluentValidation;

namespace Wolverine.FluentValidation.Internals;

public static class FluentValidationExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExecuteOne<T>(IValidator<T> validator, IFailureAction<T> failureAction, T message)
    {
        var result = validator.Validate(message);
        if (result.Errors.Any())
        {
            failureAction.Throw(message, result.Errors);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExecuteMany<T>(IReadOnlyList<IValidator<T>> validators, IFailureAction<T> failureAction,
        T message)
    {
        var validationFailures = validators
            .Select(validator => validator.Validate(message))
            .SelectMany(validationResult => validationResult.Errors)
            .Where(validationFailure => validationFailure != null)
            .ToList();

        if (validationFailures.Any())
        {
            failureAction.Throw(message, validationFailures);
        }
    }
}