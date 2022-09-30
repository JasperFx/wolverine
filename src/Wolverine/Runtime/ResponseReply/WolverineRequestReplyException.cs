using System;

namespace Wolverine.Runtime.ResponseReply;

public class WolverineRequestReplyException : Exception
{
    public WolverineRequestReplyException(string failureMessage) 
        : base("Request failed: " + failureMessage)
    {
    }
}