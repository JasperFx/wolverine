using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Wolverine.DataAnnotationsValidation.Internals;

public static class DataAnnotationsValidationExecutor
{
    // ValidationContext + Validator.TryValidateObject reflect over the runtime
    // type's [Validation*] attribute graph. T is the user-defined message type
    // that handler discovery already roots (and DataAnnotationsValidationPolicy
    // closes Validate<T> at codegen time, so trim-walkers can see the closed
    // generic instantiations). AOT consumers preserve message types via
    // TrimmerRootDescriptor or by relying on Wolverine's TypeLoadMode.Static
    // codegen pipeline. See AOT guide.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "ValidationContext / Validator.TryValidateObject reflect over the runtime message type's DataAnnotations attributes; T is handler-rooted. AOT consumers preserve via TrimmerRootDescriptor. See AOT guide.")]
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