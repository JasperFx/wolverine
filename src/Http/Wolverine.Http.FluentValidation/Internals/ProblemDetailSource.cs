using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.FluentValidation.Internals;

public class ProblemDetailSource<T> : IProblemDetailSource<T>
{
    public ProblemDetails Create(T message, IReadOnlyList<ValidationFailure> failures)
    {
        var dict =
            failures.GroupBy(x => x.PropertyName)
                .ToDictionary(x => x.Key, x => x.Select(i => i.ErrorMessage).ToArray());

        return new ValidationProblemDetails(dict) { Status = 400 };
    }
}