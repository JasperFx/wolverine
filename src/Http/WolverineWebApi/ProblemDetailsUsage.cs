using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
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

public static class NumberMessageHandler
{
    #region sample_using_problem_details_in_message_handler

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
    
    // Look at this! You can use this as an HTTP endpoint too!
    [WolverinePost("/problems2")]
    public static void Handle(NumberMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    #endregion

    public static bool Handled { get; set; }
}