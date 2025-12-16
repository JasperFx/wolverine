using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.Validation.Internals;

public class ProblemDetailSource<T> : IProblemDetailSource<T>
{
    public ProblemDetails Create(T message, ICollection<ValidationResult> failures)
    {
        var state = failures
            .SelectMany(x => x.MemberNames)
            .Distinct()
            .ToDictionary(
                x => x,
                x => failures
                    .Where(r => r.MemberNames.Any(n => n == x))
                    .Select(r => r.ErrorMessage ?? "unknown error")
                    .ToArray());

        return new ValidationProblemDetails(state) { Status = 400 };
    }
}