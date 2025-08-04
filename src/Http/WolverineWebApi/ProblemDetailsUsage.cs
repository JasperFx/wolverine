using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_ProblemDetailsUsageEndpoint

public class ProblemDetailsUsageEndpoint
{
    public ProblemDetails Before(NumberMessage message)
    {
        // If the number is greater than 5, fail with a
        // validation message
        if (message.Number > 5)
            return new ProblemDetails
            {
                Detail = "Number is bigger than 5",
                Status = 400
            };

        // All good, keep on going!
        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/problems")]
    public static string Post(NumberMessage message)
    {
        return "Ok";
    }
}

public record NumberMessage(int Number);

#endregion

#region sample_using_problem_details_in_message_handler

public static class NumberMessageHandler
{
    public static ProblemDetails Validate(NumberMessage message)
    {
        if (message.Number > 5)
        {
            return new ProblemDetails
            {
                Detail = "Number is bigger than 5",
                Status = 400
            };
        }
        
        // All good, keep on going!
        return WolverineContinue.NoProblems;
    }

    // This "Before" method would only be utilized as
    // an HTTP endpoint
    [WolverineBefore(MiddlewareScoping.HttpEndpoints)]
    public static void BeforeButOnlyOnHttp(HttpContext context)
    {
        Debug.WriteLine("Got an HTTP request for " + context.TraceIdentifier);
        CalledBeforeOnlyOnHttpEndpoints = true;
    }

    // This "Before" method would only be utilized as
    // a message handler
    [WolverineBefore(MiddlewareScoping.MessageHandlers)]
    public static void BeforeButOnlyOnMessageHandlers()
    {
        CalledBeforeOnlyOnMessageHandlers = true;
    }

    // Look at this! You can use this as an HTTP endpoint too!
    [WolverinePost("/problems2")]
    public static void Handle(NumberMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }


    // These properties are just a cheap trick in Wolverine internal tests
    public static bool Handled { get; set; }
    public static bool CalledBeforeOnlyOnMessageHandlers { get; set; }
    public static bool CalledBeforeOnlyOnHttpEndpoints { get; set; }
}
#endregion