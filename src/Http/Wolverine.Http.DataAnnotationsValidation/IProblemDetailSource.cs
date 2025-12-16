using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.DataAnnotationsValidation;

/// <summary>
///     What do you do with a validation failure? Generally assumed to throw an
///     exception. Should be registered as a singleton when possible
/// </summary>
/// <typeparam name="T"></typeparam>
// ReSharper disable once TypeParameterCanBeVariant
public interface IProblemDetailSource<T>
{
    ProblemDetails Create(T message, ICollection<ValidationResult> failures);
}