using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

namespace Wolverine.DataAnnotationsValidation.Internals;

public static class DataAnnotationsValidationExecutor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Validate<T>(T message, IServiceProvider services, IFailureAction<T> failureAction)
    {
        ArgumentNullException.ThrowIfNull(message);

        var context = new ValidationContext(message, services, null);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(message, context, results, true);

        if (!isValid)
        {
            failureAction.Throw(message, results);
        }
    }
}