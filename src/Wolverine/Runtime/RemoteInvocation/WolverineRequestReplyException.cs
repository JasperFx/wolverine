namespace Wolverine.Runtime.RemoteInvocation;

public class WolverineRequestReplyException : Exception
{
    public WolverineRequestReplyException(string failureMessage)
        : base("Request failed: " + failureMessage)
    {
    }
}