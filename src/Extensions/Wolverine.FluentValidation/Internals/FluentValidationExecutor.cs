using System.Runtime.CompilerServices;
using FluentValidation;
using FluentValidation.Results;

namespace Wolverine.FluentValidation.Internals;

public static class FluentValidationExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task ExecuteOne<T>(IValidator<T> validator, IFailureAction<T> failureAction, T message)
    {
        var result = await validator.ValidateAsync(message);
        if (result.Errors.Count != 0)
        {
            failureAction.Throw(message, result.Errors);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task ExecuteMany<T>(IEnumerable<IValidator<T>> validators, IFailureAction<T> failureAction,
        T message)
    {
        var failures = new List<ValidationFailure>();

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(message);
            if (result is not null && result.Errors.Count != 0)
            {
                failures.AddRange(result.Errors);
            }
        }

        if (failures.Count != 0)
        {
            failureAction.Throw(message, failures);
        }
    }
}