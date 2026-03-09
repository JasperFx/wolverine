using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_requirement_result_http_validation

public record RequirementResultHttpMessage(int Number);

public static class RequirementResultHttpEndpoint
{
    public static RequirementResult Validate(RequirementResultHttpMessage message)
    {
        if (message.Number > 10)
        {
            return new RequirementResult(HandlerContinuation.Stop, ["Number must be 10 or less"]);
        }

        return new RequirementResult(HandlerContinuation.Continue, []);
    }

    [WolverinePost("/requirement-result/sync")]
    public static string Post(RequirementResultHttpMessage message) => "Ok";
}

#endregion

#region sample_requirement_result_http_async

public record RequirementResultHttpAsyncMessage(int Number);

public static class RequirementResultHttpAsyncEndpoint
{
    public static Task<RequirementResult> ValidateAsync(RequirementResultHttpAsyncMessage message)
    {
        if (message.Number > 10)
        {
            return Task.FromResult(new RequirementResult(HandlerContinuation.Stop, ["Number must be 10 or less"]));
        }

        return Task.FromResult(new RequirementResult(HandlerContinuation.Continue, []));
    }

    [WolverinePost("/requirement-result/async")]
    public static string Post(RequirementResultHttpAsyncMessage message) => "Ok";
}

#endregion

#region sample_requirement_result_http_empty_messages

public record RequirementResultHttpEmptyMessagesMessage(int Number);

public static class RequirementResultHttpEmptyMessagesEndpoint
{
    public static RequirementResult Validate(RequirementResultHttpEmptyMessagesMessage message)
    {
        if (message.Number > 10)
        {
            return new RequirementResult(HandlerContinuation.Stop, []);
        }

        return new RequirementResult(HandlerContinuation.Continue, []);
    }

    [WolverinePost("/requirement-result/empty-messages")]
    public static string Post(RequirementResultHttpEmptyMessagesMessage message) => "Ok";
}

#endregion
