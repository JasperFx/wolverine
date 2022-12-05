using FluentValidation;
using FluentValidation.Results;

namespace Wolverine.FluentValidation.Internals;

/// <summary>
///     Default "exception throwing" handler for the Fluent Validation middleware. Throws
///     a ValidationException
/// </summary>
/// <typeparam name="T"></typeparam>
public class FailureAction<T> : IFailureAction<T>
{
    public void Throw(T message, IReadOnlyList<ValidationFailure> failures)
    {
        throw new ValidationException($"Validation failure on: {message}", failures);
    }
}