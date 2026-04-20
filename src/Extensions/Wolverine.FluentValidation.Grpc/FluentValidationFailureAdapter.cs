using FluentValidation;
using Google.Rpc;
using Wolverine.Grpc;

namespace Wolverine.FluentValidation.Grpc;

/// <summary>
///     <see cref="IValidationFailureAdapter"/> implementation for FluentValidation's
///     <see cref="ValidationException"/>. Produces one
///     <see cref="BadRequest.Types.FieldViolation"/> per <c>ValidationFailure</c>, mapping
///     <c>PropertyName</c> to <c>Field</c> and <c>ErrorMessage</c> to <c>Description</c>.
/// </summary>
/// <remarks>
///     Ship separately from the core <c>Wolverine.Grpc</c> package so that the core
///     assembly stays free of any FluentValidation dependency. Registered via
///     <see cref="WolverineOptionsExtensions.UseFluentValidationGrpcErrorDetails(WolverineOptions)"/>.
/// </remarks>
public sealed class FluentValidationFailureAdapter : IValidationFailureAdapter
{
    public bool CanHandle(Exception exception) => exception is ValidationException;

    public IEnumerable<BadRequest.Types.FieldViolation> ToFieldViolations(Exception exception)
    {
        var validation = (ValidationException)exception;
        foreach (var failure in validation.Errors)
        {
            yield return new BadRequest.Types.FieldViolation
            {
                Field = failure.PropertyName ?? string.Empty,
                Description = failure.ErrorMessage ?? string.Empty
            };
        }
    }
}
