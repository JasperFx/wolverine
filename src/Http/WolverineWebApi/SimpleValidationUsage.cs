using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_simple_validation_http_ienumerable

public record SimpleValidateHttpEnumerableMessage(int Number);

public static class SimpleValidationHttpEnumerableEndpoint
{
    public static IEnumerable<string> Validate(SimpleValidateHttpEnumerableMessage message)
    {
        if (message.Number > 10)
        {
            yield return "Number must be 10 or less";
        }
    }

    [WolverinePost("/simple-validation/ienumerable")]
    public static string Post(SimpleValidateHttpEnumerableMessage message) => "Ok";
}

#endregion

#region sample_simple_validation_http_string_array

public record SimpleValidateHttpStringArrayMessage(int Number);

public static class SimpleValidationHttpStringArrayEndpoint
{
    public static string[] Validate(SimpleValidateHttpStringArrayMessage message)
    {
        if (message.Number > 10)
        {
            return ["Number must be 10 or less"];
        }

        return [];
    }

    [WolverinePost("/simple-validation/string-array")]
    public static string Post(SimpleValidateHttpStringArrayMessage message) => "Ok";
}

#endregion

#region sample_simple_validation_http_async

public record SimpleValidateHttpAsyncMessage(int Number);

public static class SimpleValidationHttpAsyncEndpoint
{
    public static Task<string[]> ValidateAsync(SimpleValidateHttpAsyncMessage message)
    {
        if (message.Number > 10)
        {
            return Task.FromResult(new[] { "Number must be 10 or less" });
        }

        return Task.FromResult(Array.Empty<string>());
    }

    [WolverinePost("/simple-validation/async")]
    public static string Post(SimpleValidateHttpAsyncMessage message) => "Ok";
}

#endregion

#region sample_simple_validation_http_valuetask

public record SimpleValidateHttpValueTaskMessage(int Number);

public static class SimpleValidationHttpValueTaskEndpoint
{
    public static ValueTask<string[]> ValidateAsync(SimpleValidateHttpValueTaskMessage message)
    {
        if (message.Number > 10)
        {
            return new ValueTask<string[]>(new[] { "Number must be 10 or less" });
        }

        return new ValueTask<string[]>(Array.Empty<string>());
    }

    [WolverinePost("/simple-validation/valuetask")]
    public static string Post(SimpleValidateHttpValueTaskMessage message) => "Ok";
}

#endregion

#region sample_simple_validation_http_validationoutcome

public record SimpleValidateHttpValidationOutcomeMessage(int Number);

public static class SimpleValidationHttpValidationOutcomeEndpoint
{
    public static ValidationOutcome ValidateAsync(SimpleValidateHttpValidationOutcomeMessage message)
    {
        if (message.Number > 10)
        {
            return [new(nameof(SimpleValidateHttpValidationOutcomeMessage.Number), "Number must be 10 or less")];
        }

        return [];
    }

    [WolverinePost("/simple-validation/validationoutcome")]
    public static string Post(SimpleValidateHttpValidationOutcomeMessage message) => "Ok";
}

#endregion

#region sample_simple_validation_http_validationoutcome_async

public record SimpleValidateHttpValidationOutcomeAsyncMessage(int Number);

public static class SimpleValidationHttpValidationOutcomeAsyncEndpoint
{
    public static Task<ValidationOutcome> ValidateAsync(SimpleValidateHttpValidationOutcomeAsyncMessage message)
    {
        if (message.Number > 10)
        {
            return Task.FromResult<ValidationOutcome>(
                [new(nameof(SimpleValidateHttpValidationOutcomeAsyncMessage.Number), "Number must be 10 or less")]
            );
        }

        return Task.FromResult<ValidationOutcome>([]);
    }

    [WolverinePost("/simple-validation/validationoutcomeasync")]
    public static string Post(SimpleValidateHttpValidationOutcomeAsyncMessage message) => "Ok";
}

#endregion