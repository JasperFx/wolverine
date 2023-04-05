using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.FluentValidation;

/// <summary>
///     What do you do with a validation failure? Generally assumed to throw an
///     exception. Should be registered as a singleton when possible
/// </summary>
/// <typeparam name="T"></typeparam>
// ReSharper disable once TypeParameterCanBeVariant
public interface IProblemDetailSource<T>
{
    ProblemDetails Create(T message, IReadOnlyList<ValidationFailure> failures);
}