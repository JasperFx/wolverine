using GreeterWithGrpcErrors.Messages;

namespace GreeterWithGrpcErrors.Server;

/// <summary>
///     Raised by <see cref="FarewellHandler"/> when the request violates a server-side
///     policy. Mapped to <c>Code.FailedPrecondition</c> + <see cref="Google.Rpc.PreconditionFailure"/>
///     in <c>Program.cs</c> via the rich-details <c>MapException</c> hook.
/// </summary>
public sealed class GreetingForbiddenException : Exception
{
    public GreetingForbiddenException(string subject, string reason)
        : base($"Greeting forbidden for '{subject}': {reason}")
    {
        Subject = subject;
        Reason = reason;
    }

    public string Subject { get; }
    public string Reason { get; }
}

public static class GreetHandler
{
    public static GreetReply Handle(GreetRequest request)
        => new() { Message = $"Hello, {request.Name}" };
}

public static class FarewellHandler
{
    private static readonly HashSet<string> Banned = new(StringComparer.OrdinalIgnoreCase)
    {
        "voldemort",
        "moriarty"
    };

    public static FarewellReply Handle(FarewellRequest request)
    {
        if (Banned.Contains(request.Name))
        {
            throw new GreetingForbiddenException(request.Name, "name is on the banned list");
        }

        return new FarewellReply { Message = $"Goodbye, {request.Name}" };
    }
}
