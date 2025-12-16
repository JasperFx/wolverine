using System.ComponentModel.DataAnnotations;

namespace Wolverine.DataAnnotationsValidation.Internals;

/// <summary>
///     Default "exception throwing" handler for the DataAnnotations Validation middleware. Throws
///     a ValidationException
/// </summary>
/// <typeparam name="T"></typeparam>
public class FailureAction<T> : IFailureAction<T>
{
    public void Throw(T message, ICollection<ValidationResult> failures)
    {
        throw new ValidationException($"Validation failure on: {message}", failures);
    }
}

public class ValidationException(string message, ICollection<ValidationResult> failures) : Exception(message)
{
    public ICollection<ValidationResult> Failures = failures;
}