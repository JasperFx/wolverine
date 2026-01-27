namespace Wolverine.SignalR;

public class InvalidWolverineSignalROperationException : Exception
{
    public InvalidWolverineSignalROperationException(string? message) : base(message)
    {
    }
}