using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Validation.Internals;

public class DataAnnotationsHttpValidationExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IResult Validate<T>(IProblemDetailSource<T> source, IServiceProvider services, T message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var context = new ValidationContext(message, services, null);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(message, context, results, true);

        if (!isValid)
        {
            var details = source.Create(message, results);
            return Results.Problem(details);
        }

        return WolverineContinue.Result();
    }

#if NET11_0_OR_GREATER
    /// <summary>
    ///     .NET 11's asynchronous DataAnnotations validation (GH-5147): runs <see cref="AsyncValidationAttribute" />
    ///     and <see cref="IAsyncValidatableObject" /> through their async members rather than the synchronous
    ///     fallback <see cref="Validator.TryValidateObject(object, ValidationContext, ICollection{ValidationResult}, bool)" />
    ///     would call. Only used for request types that declare either, so every other endpoint keeps the
    ///     allocation-free synchronous path.
    /// </summary>
    public static async Task<IResult> ValidateAsync<T>(IProblemDetailSource<T> source, IServiceProvider services,
        T message, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(message);

        var context = new ValidationContext(message, services, null);
        var results = new List<ValidationResult>();
        var isValid = await Validator.TryValidateObjectAsync(message, context, results, true, cancellation)
            .ConfigureAwait(false);

        if (!isValid)
        {
            var details = source.Create(message, results);
            return Results.Problem(details);
        }

        return WolverineContinue.Result();
    }

    /// <summary>
    ///     Does <paramref name="type" /> need <see cref="ValidateAsync{T}" />: an <see cref="AsyncValidationAttribute" />
    ///     on the type or one of its properties, or an <see cref="IAsyncValidatableObject" /> implementation.
    /// </summary>
    public static bool RequiresAsyncValidation(Type type)
    {
        if (typeof(IAsyncValidatableObject).IsAssignableFrom(type)) return true;
        if (type.GetCustomAttributes(typeof(AsyncValidationAttribute), true).Length > 0) return true;

        return type.GetProperties().Any(p => p.GetCustomAttributes(typeof(AsyncValidationAttribute), true).Length > 0);
    }
#endif
}