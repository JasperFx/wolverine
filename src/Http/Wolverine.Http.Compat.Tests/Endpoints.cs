using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
#if NET11_0_OR_GREATER
using Microsoft.AspNetCore.Routing;
#endif

namespace Wolverine.Http.Compat.Tests;

public record CompatDto(string Name);

public class CsrfEndpoints
{
    [WolverineGet("/compat/csrf/get")]
    public string Get() => "got";

    [WolverinePost("/compat/csrf/form")]
    public string PostForm([FromForm] string name) => "form:" + name;

    [WolverinePost("/compat/csrf/file")]
    public string PostFile(IFormFile file) => "file:" + file.FileName;

    [WolverinePost("/compat/csrf/json")]
    public string PostJson(CompatDto dto) => "json:" + dto.Name;

    [WolverinePost("/compat/csrf/json-required")]
    [ValidateAntiforgery]
    public string PostJsonRequired(CompatDto dto) => "json-required:" + dto.Name;

    [WolverinePost("/compat/csrf/form-disabled")]
    [DisableAntiforgery]
    public string PostFormDisabled([FromForm] string name) => "form-disabled:" + name;
}

// ─── Validation ───

public class ValidatedRequest
{
    [Required] [StringLength(10)] public string? Name { get; set; }
}

/// <summary>Overrides .NET 11's ValidationAttribute.FormatMessage hook; plain FormatErrorMessage on .NET 10.</summary>
public class ShoutingRequiredAttribute : RequiredAttribute
{
    public ShoutingRequiredAttribute()
    {
        ErrorMessage = "{0} is missing";
    }

#if NET11_0_OR_GREATER
    public override string FormatMessage(string format, string name) => base.FormatMessage(format, name).ToUpperInvariant();
#else
    public override string FormatErrorMessage(string name) => base.FormatErrorMessage(name).ToUpperInvariant();
#endif
}

public class FormattedRequest
{
    [ShoutingRequired] public string? Name { get; set; }
}

public class ValidationEndpoints
{
    [WolverinePost("/compat/validation/sync")]
    public string PostSync(ValidatedRequest request) => "ok:" + request.Name;

    [WolverinePost("/compat/validation/formatted")]
    public string PostFormatted(FormattedRequest request) => "ok:" + request.Name;

#if NET11_0_OR_GREATER
    [WolverinePost("/compat/validation/async-attribute")]
    public string PostAsyncAttribute(AsyncAttributeRequest request) => "ok:" + request.Name;

    [WolverinePost("/compat/validation/async-object")]
    public string PostAsyncObject(AsyncObjectRequest request) => "ok:" + request.Name;
#endif
}

#if NET11_0_OR_GREATER
/// <summary>An async rule whose synchronous fallback would let everything through, to prove which one ran.</summary>
public class NotTakenAttribute : AsyncValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        ValidationResult.Success;

    protected override async Task<ValidationResult?> IsValidAsync(object? value, ValidationContext validationContext,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        return value as string == "taken"
            ? new ValidationResult($"'{value}' is already taken", [validationContext.MemberName!])
            : ValidationResult.Success;
    }
}

public class AsyncAttributeRequest
{
    [NotTaken] public string? Name { get; set; }
}

public class AsyncObjectRequest : IAsyncValidatableObject
{
    public string? Name { get; set; }

    // The synchronous fallback finds nothing wrong, so a refusal can only come from ValidateAsync
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => [];

    public async IAsyncEnumerable<ValidationResult> ValidateAsync(ValidationContext validationContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (Name == "taken") yield return new ValidationResult("async object says no", [nameof(Name)]);
    }
}

// ─── [ShortCircuit] ───

// Hosts in this assembly that call RequireAntiforgeryOnAll() would otherwise refuse these at startup
[DisableAntiforgery]
public class ShortCircuitEndpoints
{
    [WolverineGet("/compat/short-circuit")]
    [ShortCircuit]
    public string Get() => "short-circuited";

    [WolverineGet("/compat/not-short-circuited")]
    public string GetNormally() => "normal";

    [WolverinePost("/compat/short-circuit/form-disabled")]
    [ShortCircuit]
    public string PostFormWithoutCsrf([FromForm] string name) => "short-circuited:" + name;
}

/// <summary>Kept out of discovery: refused at startup, see short_circuit_tests.</summary>
[WolverineIgnore]
public class ShortCircuitFormEndpoints
{
    [WolverinePost("/compat/short-circuit/form")]
    [ShortCircuit]
    public string PostForm([FromForm] string name) => name;

    [WolverinePost("/compat/short-circuit/json-required")]
    [ShortCircuit]
    [ValidateAntiforgery]
    public string PostJsonRequired(CompatDto dto) => dto.Name;
}
#endif
