using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi.Antiforgery;

public static class AntiforgeryEndpoints
{
    #region sample_antiforgery_form_endpoint
    // Antiforgery validation is automatic for [FromForm] endpoints
    [WolverinePost("/api/form/contact")]
    public static string SubmitContactForm([FromForm] string name, [FromForm] string email)
    {
        return $"Received from {name} ({email})";
    }
    #endregion

    #region sample_antiforgery_disabled
    // Opt out of antiforgery validation
    [DisableAntiforgery]
    [WolverinePost("/api/form/webhook")]
    public static string WebhookReceiver([FromForm] string payload)
    {
        return $"Processed: {payload}";
    }
    #endregion

    #region sample_antiforgery_explicit
    // Opt in to antiforgery validation for non-form endpoints
    [ValidateAntiforgery]
    [WolverinePost("/api/secure/action")]
    public static string SecureAction(SecureCommand command)
    {
        return $"Executed: {command.Action}";
    }
    #endregion
}

public record SecureCommand(string Action);
