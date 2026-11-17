using Alba;

namespace Wolverine.Http.Compat.Tests;

/// <summary>
///     GH-5147: the DataAnnotations middleware on .NET 11 runs async validation (AsyncValidationAttribute,
///     IAsyncValidatableObject, Validator.TryValidateObjectAsync) where a request type declares it, and the
///     ValidationAttribute.FormatMessage hook reaches the ProblemDetails.
/// </summary>
public class data_annotations_validation : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await CompatHost.For();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task synchronous_rules_still_use_the_synchronous_executor()
    {
        _host.Chain("/compat/validation/sync").SourceCode!.ShouldContain("DataAnnotationsHttpValidationExecutor.Validate<");

        await _host.Scenario(x =>
        {
            x.Post.Json(new ValidatedRequest { Name = "much too long a name" }).ToUrl("/compat/validation/sync");
            x.StatusCodeShouldBe(400);
        });

        await _host.Scenario(x =>
        {
            x.Post.Json(new ValidatedRequest { Name = "ok" }).ToUrl("/compat/validation/sync");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task a_custom_message_format_reaches_the_problem_details()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new FormattedRequest()).ToUrl("/compat/validation/formatted");
            x.StatusCodeShouldBe(400);
        });

        (await result.ReadAsTextAsync()).ShouldContain("NAME IS MISSING");
    }

#if NET11_0_OR_GREATER
    [Fact]
    public void async_rules_use_the_async_executor()
    {
        _host.Chain("/compat/validation/async-attribute").SourceCode!
            .ShouldContain("DataAnnotationsHttpValidationExecutor.ValidateAsync<");
        _host.Chain("/compat/validation/async-object").SourceCode!
            .ShouldContain("DataAnnotationsHttpValidationExecutor.ValidateAsync<");
    }

    [Fact]
    public async Task an_async_validation_attribute_runs_its_async_rule()
    {
        // NotTakenAttribute's synchronous IsValid always succeeds; only the async rule refuses "taken"
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new AsyncAttributeRequest { Name = "taken" }).ToUrl("/compat/validation/async-attribute");
            x.StatusCodeShouldBe(400);
        });

        (await result.ReadAsTextAsync()).ShouldContain("'taken' is already taken");

        await _host.Scenario(x =>
        {
            x.Post.Json(new AsyncAttributeRequest { Name = "free" }).ToUrl("/compat/validation/async-attribute");
            x.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task an_async_validatable_object_runs_its_async_rule()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new AsyncObjectRequest { Name = "taken" }).ToUrl("/compat/validation/async-object");
            x.StatusCodeShouldBe(400);
        });

        (await result.ReadAsTextAsync()).ShouldContain("async object says no");

        await _host.Scenario(x =>
        {
            x.Post.Json(new AsyncObjectRequest { Name = "free" }).ToUrl("/compat/validation/async-object");
            x.StatusCodeShouldBeOk();
        });
    }
#endif
}
