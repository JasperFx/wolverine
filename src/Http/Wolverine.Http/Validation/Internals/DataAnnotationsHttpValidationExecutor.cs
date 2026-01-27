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
}